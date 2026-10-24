using System.Threading.Channels;

using Andromeld.Protocol;
using Andromeld.Testing;

namespace Andromeld.Protocol.Tests;

public static partial class Program
{
    private static async Task CheckNotificationsAsync()
    {
        using CancellationTokenSource stop = new(TimeSpan.FromSeconds(10));
        await using MockPhone phone = await MockPhone.StartAsync();
        await using AndromeldClient client = new();
        await client.ConnectAsync(phone.Endpoint, "synthetic-notification-client", null, null, stop.Token);
        Channel<NotificationSnapshot> snapshots = Channel.CreateUnbounded<NotificationSnapshot>();
        client.NotificationsReceived += snapshot => snapshots.Writer.TryWrite(snapshot);
        await client.SetNotificationsEnabledAsync(true, stop.Token);
        Check((await snapshots.Reader.ReadAsync(stop.Token)).Active.Count == 0 && client.Capabilities.Contains("notification"),
            "notification capability and reqId-correlated notif-state subscribe use the control channel");

        PhoneNotification notification = new() { Key = "0|test.app0|42|tag=消息;|0", PackageName = "test.app0", UserId = 10,
            Title = "消息 <&> \"✓\"", Text = "来自手机\nUnicode ✓", Clearable = true, PostTime = 1000, UpdateTime = 1001,
            Actions = [new() { Index = 1, Title = "已读" }, new() { Index = 7, Title = "回复 <&> '✓'", HasRemoteInput = true }] };
        await phone.PushNotificationsAsync(notification, notification with { Key = "removed", RemovedAt = 5 },
            notification with { Key = "hidden", SystemHidden = true }, new { key = "bad", packageName = "test.app", userId = "invalid" }, new { key = "no-package" });
        NotificationSnapshot snapshot = await snapshots.Reader.ReadAsync(stop.Token);
        Check(snapshot.Active.Count == 1 && snapshot.Active[0] with { Actions = notification.Actions } == notification &&
            snapshot.Active[0].Actions.SequenceEqual(notification.Actions),
            "fragmented notification snapshot preserves Unicode, opaque key and Android user while filtering malformed/hidden/removed records");

        await phone.PushNotificationsAsync(new { key = "bounded-actions", packageName = "test.app0", actions = new object?[]
        {
            null, new { index = -1, hasRemoteInput = true }, new { index = 65, hasRemoteInput = true }, new { hasRemoteInput = true },
            new { index = 64, title = "回复", hasRemoteInput = true }
        } }, new { key = "null-actions", packageName = "test.app0", actions = (object?)null });
        snapshot = await snapshots.Reader.ReadAsync(stop.Token);
        Check(snapshot.Active.Count == 2 && snapshot.Active[0].Actions is [{ Index: 64, HasRemoteInput: true }] &&
            snapshot.Active[1] is { Actions.Count: 0, ContentIntentTargetKind: "unknown" },
            "notification actions preserve server indexes and filter missing/out-of-range/null actions without losing valid notifications");

        await client.DismissNotificationAsync(notification.Key, stop.Token);
        var dismiss = phone.NotificationRequests.Last();
        Check(dismiss.GetProperty("command").GetInt32() == 2 && dismiss.GetProperty("key").GetString() == notification.Key,
            "dismiss uses notif-cmd command 2 and preserves the complete Android notification key");
        await client.OpenNotificationAsync(notification, "mirror-42", stop.Token);
        var open = phone.NotificationRequests.Last();
        Check(open.GetProperty("command").GetInt32() == 6 && open.GetProperty("sessionId").GetString() == "mirror-42" &&
            open.GetProperty("fallbackPkg").GetString() == notification.PackageName && open.GetProperty("fallbackUserId").GetInt32() == 10,
            "notification open targets the mirror session with package and user fallback");
        await client.OpenNotificationAsync(notification, null, stop.Token);
        Check(!phone.NotificationRequests.Last().TryGetProperty("sessionId", out _), "non-activity notification intents need no mirror session");

        await client.ReplyToNotificationAsync(notification.Key, 7, "  合成回复 <&> ✓\n下一行  ", "mirror-reply", stop.Token);
        var reply = phone.NotificationRequests.Last();
        Check(reply.GetProperty("command").GetInt32() == 8 && reply.GetProperty("key").GetString() == notification.Key &&
            reply.GetProperty("actionIndex").GetInt32() == 7 && reply.GetProperty("replyText").GetString() == "合成回复 <&> ✓\n下一行" &&
            reply.GetProperty("sessionId").GetString() == "mirror-reply" && !reply.TryGetProperty("fallbackPkg", out _),
            "inline reply uses command 8, the original action index, trimmed Unicode text and the mirror session without open-intent fallback");
        await client.ReplyToNotificationAsync(notification.Key, 64, new string('x', 4095) + "😀尾", "mirror-reply", stop.Token);
        Check(phone.NotificationRequests.Last().GetProperty("replyText").GetString() == new string('x', 4095),
            "long notification replies obey the helper's 4096 UTF-16-unit limit without splitting a surrogate pair");
        int requests = phone.NotificationRequests.Count;
        Func<Task>[] invalidReplies =
        [
            () => client.ReplyToNotificationAsync("", 7, "text", "session", stop.Token),
            () => client.ReplyToNotificationAsync(new string('k', 513), 7, "text", "session", stop.Token),
            () => client.ReplyToNotificationAsync(notification.Key, -1, "text", "session", stop.Token),
            () => client.ReplyToNotificationAsync(notification.Key, 65, "text", "session", stop.Token),
            () => client.ReplyToNotificationAsync(notification.Key, 7, " \n\t", "session", stop.Token),
            () => client.ReplyToNotificationAsync(notification.Key, 7, "text", "", stop.Token),
            () => client.ReplyToNotificationAsync(notification.Key, 7, "text", new string('s', 65), stop.Token)
        ];
        int rejected = 0;
        foreach (Func<Task> invalid in invalidReplies)
        {
            try { await invalid(); }
            catch (ArgumentException) { rejected++; }
        }
        Check(rejected == invalidReplies.Length && phone.NotificationRequests.Count == requests,
            "invalid reply keys, indexes, blank text and sessions are rejected locally before the shared control channel is touched");

        phone.NotificationCommandError = "not-found";
        try { await client.DismissNotificationAsync(notification.Key, stop.Token); throw new Exception("Expected notification command failure"); }
        catch (IOException ex) { Check(ex.Message.Contains("not-found"), "notification command errors remain observable"); }
        try { await client.ReplyToNotificationAsync(notification.Key, 7, "text", "mirror-reply", stop.Token); throw new Exception("Expected reply command failure"); }
        catch (IOException ex) { Check(ex.Message.Contains("not-found"), "phone reply failures remain observable without disconnecting"); }
        phone.NotificationCommandError = null;
        await client.SetNotificationsEnabledAsync(false, stop.Token);
        phone.NotificationSubscriptionError = "no-permission";
        try { await client.SetNotificationsEnabledAsync(true, stop.Token); throw new Exception("Expected notification permission failure"); }
        catch (IOException ex) { Check(ex.Message.Contains("no-permission"), "missing Android notification permission is reported"); }
        Check((await client.GetAppsAsync(stop.Token)).Length == 18, "notification failures and malformed records leave the main connection usable");
    }
}
