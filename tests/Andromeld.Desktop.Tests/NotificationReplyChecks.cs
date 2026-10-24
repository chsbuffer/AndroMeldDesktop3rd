using Andromeld.Desktop.Services;
using Andromeld.Protocol;
using Andromeld.Testing;

using Avalonia.Threading;

namespace Andromeld.Desktop.Tests;

internal static class NotificationReplyChecks
{
    internal static async Task RunAsync(CancellationToken ct)
    {
        await using MockPhone phone = await MockPhone.StartAsync();
        await using AndromeldClient client = new();
        await client.ConnectAsync(phone.Endpoint, "synthetic-inline-reply", null, null, ct);
        NotificationChecks.Manager manager = new();
        List<Exception> errors = [];
        TaskCompletionSource<string>? sessionGate = null;
        PhoneNotification? sessionNotification = null;
        int sessions = 0;
        Task<string> GetSessionAsync(PhoneNotification notification, CancellationToken token)
        {
            Dispatcher.UIThread.VerifyAccess();
            token.ThrowIfCancellationRequested();
            sessionNotification = notification;
            sessions++;
            return sessionGate?.Task ?? Task.FromResult("mirror-reply");
        }

        await using NotificationSharing sharing = new(client, manager, GetSessionAsync, true, errors.Add, ct);
        await sharing.StartAsync();
        await BarrierAsync(client, ct);
        PhoneNotification record = NotificationChecks.Existing with
        {
            Key = "0|test.app0|42|tag=合成回复;|10", UserId = 10, ContentIntentTargetKind = "broadcast",
            Actions = [new() { Index = 2, Title = "已读" }, new() { Index = 9, Title = "回复 <&> '✓'", HasRemoteInput = true },
                new() { Index = 11, Title = "其他回复", HasRemoteInput = true }]
        };
        await phone.PushNotificationsAsync(record);
        await UntilAsync(() => manager.Active.Count == 1, ct);
        NotificationChecks.Toast toast = manager.Created.Last();
        string replyTag = toast.ReplyActionTag ?? throw new InvalidOperationException("Expected inline reply input");
        Program.Check(toast.Actions.Count == 3 && toast.Actions.Count(action => action.Tag == replyTag) == 1 &&
            toast.Actions[0].Caption.Contains("回复", StringComparison.Ordinal),
            "reply-capable native notifications expose one RemoteInput action beside open/dismiss");
        int shows = manager.Shows;
        await phone.PushNotificationsAsync(record);
        await BarrierAsync(client, ct);
        Program.Check(manager.Shows == shows, "equivalent notification action arrays do not retrigger native alerts");

        await manager.CompleteAsync(toast.Id, replyTag, userData: " \n\t");
        await BarrierAsync(client, ct);
        await manager.CompleteAsync(toast.Id, replyTag, userData: 42);
        await BarrierAsync(client, ct);
        await manager.CompleteAsync(toast.Id, "reply-11", activated: true, userData: "wrong action");
        await BarrierAsync(client, ct);
        Program.Check(sessions == 0 && Commands(phone) == 0 && manager.Active.Count == 1,
            "blank, non-text and unexposed reply callbacks neither open a mirror nor send a phone command");

        await manager.CompleteAsync(toast.Id, replyTag, activated: true, userData: "  合成回复 <&> ✓\n下一行  ");
        await UntilAsync(() => manager.Active.Count == 0, ct);
        var reply = phone.NotificationRequests.Last();
        Program.Check(sessions == 1 && sessionNotification?.UserId == 10 && sessionNotification.PackageName == record.PackageName &&
            reply.GetProperty("command").GetInt32() == 8 && reply.GetProperty("actionIndex").GetInt32() == 9 &&
            reply.GetProperty("key").GetString() == record.Key && reply.GetProperty("replyText").GetString() == "合成回复 <&> ✓\n下一行" &&
            reply.GetProperty("sessionId").GetString() == "mirror-reply" && errors.Count == 0,
            "worker-thread inline reply forwards text and the original action index through the correct user's mirror, even for broadcast intents");

        record = record with { Key = "reply-rejected" };
        await phone.PushNotificationsAsync(record);
        await UntilAsync(() => manager.Active.Count == 1, ct);
        toast = manager.Created.Last();
        phone.NotificationCommandError = "action-unavailable";
        await manager.CompleteAsync(toast.Id, replyTag, userData: "synthetic rejected reply");
        await UntilAsync(() => errors.Count == 1, ct);
        Program.Check(errors[0].Message.Contains("action-unavailable", StringComparison.Ordinal) && manager.Active.Count == 1 &&
            (await client.GetAppsAsync(ct)).Length == 18, "phone reply rejection is reported and preserves the notification and device connection");
        phone.NotificationCommandError = null;
        int commands = Commands(phone);
        await phone.PushNotificationsAsync(record with { Actions = [] });
        await UntilAsync(() => toast.ReplyActionTag is null, ct);
        await manager.CompleteAsync(toast.Id, replyTag, activated: true, userData: "stale reply");
        await BarrierAsync(client, ct);
        Program.Check(toast.Actions.All(action => !action.Tag.StartsWith("reply-", StringComparison.Ordinal)) && Commands(phone) == commands,
            "notification updates remove the input and ignore callbacks for a withdrawn reply action");

        await phone.PushNotificationsAsync(record);
        await UntilAsync(() => toast.ReplyActionTag is not null, ct);
        sessionGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int previousSessions = sessions;
        await manager.CompleteAsync(toast.Id, replyTag, userData: "reply while mirror starts");
        await UntilAsync(() => sessions == previousSessions + 1, ct);
        record = record with { Actions = [new() { Index = 12, Title = "新回复动作", HasRemoteInput = true }] };
        await phone.PushNotificationsAsync(record);
        await UntilAsync(() => toast.ReplyActionTag != replyTag, ct);
        sessionGate.SetResult("mirror-reply");
        await BarrierAsync(client, ct);
        Program.Check(Commands(phone) == commands && manager.Active.Count == 1,
            "changing the phone reply action while a mirror starts cancels the outdated send");

        sessionGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        previousSessions = sessions;
        await manager.CompleteAsync(toast.Id, toast.ReplyActionTag, userData: "reply during disconnect");
        await UntilAsync(() => sessions == previousSessions + 1, ct);
        sharing.Deactivate();
        sessionGate.SetResult("retired-mirror");
        await BarrierAsync(client, ct);
        await manager.CompleteAsync(toast.Id, toast.ReplyActionTag, userData: "late reply");
        await BarrierAsync(client, ct);
        Program.Check(Commands(phone) == commands && manager.Active.Count == 0 && errors.Count == 1,
            "deactivation cancels an in-flight reply and ignores late native callbacks without sending to a retired connection");
        await sharing.DisposeAsync();

        await using NotificationSharing withoutInput = new(client, manager, GetSessionAsync, false, errors.Add, ct);
        await withoutInput.StartAsync();
        await BarrierAsync(client, ct);
        await phone.PushNotificationsAsync(record);
        await UntilAsync(() => manager.Active.Count == 1, ct);
        toast = manager.Created.Last();
        await manager.CompleteAsync(toast.Id, "reply-12", activated: true, userData: "unsupported input");
        await BarrierAsync(client, ct);
        Program.Check(toast.ReplyActionTag is null && toast.Actions.All(action => !action.Tag.StartsWith("reply-", StringComparison.Ordinal)) &&
            Commands(phone) == commands, "native backends without text input keep open/dismiss and expose no unusable reply button");
    }

    private static int Commands(MockPhone phone) => phone.NotificationRequests.Count(request => request.TryGetProperty("command", out _));

    private static async Task BarrierAsync(AndromeldClient client, CancellationToken ct)
    {
        await client.GetDeviceInformationAsync(ct);
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
    }

    private static async Task UntilAsync(Func<bool> condition, CancellationToken ct)
    {
        while (!condition()) await Task.Delay(10, ct);
    }
}
