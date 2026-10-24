using System.Text.Json;

using Andromeld.Desktop.Services;
using Andromeld.Desktop.ViewModels;
using Andromeld.Desktop.Views;
using Andromeld.Protocol;
using Andromeld.Testing;

using Avalonia.Controls;
using Avalonia.Labs.Notifications;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

using ReactiveUI.Primitives;

namespace Andromeld.Desktop.Tests;

internal static class NotificationChecks
{
    internal sealed class Manager : INativeNotificationManager
    {
        private uint _nextId;
        internal Dictionary<uint, INativeNotification> Active { get; } = [];
        public IReadOnlyDictionary<uint, INativeNotification> ActiveNotifications => Active;
        internal List<Toast> Created { get; } = [];
        internal int Shows { get; set; }
        internal bool Available { get; set; } = true;
        public event EventHandler<NativeNotificationCompletedEventArgs>? NotificationCompleted;
        public INativeNotification? CreateNotification(string? category)
        {
            Dispatcher.UIThread.VerifyAccess();
            if (!Available) return null;
            Toast toast = new(++_nextId, this);
            Created.Add(toast);
            return toast;
        }
        public void CloseAll() { foreach (var toast in Active.Values.ToArray()) toast.Close(); }
        internal Task CompleteAsync(uint id, string? action = null, bool activated = false, object? userData = null) => Task.Run(() =>
            NotificationCompleted?.Invoke(this, new NativeNotificationCompletedEventArgs { NotificationId = id, ActionTag = action, IsActivated = activated, UserData = userData }));
    }

    internal sealed class Toast(uint id, Manager manager) : INativeNotification
    {
        public uint Id => id;
        public string Category => "default";
        public string? Title { get; set; }
        public string? Tag { get; set; }
        public string? Message { get; set; }
        public TimeSpan? Expiration { get; set; }
        public Bitmap? Icon { get; set; }
        public string? ReplyActionTag { get; set; }
        public IReadOnlyList<NativeNotificationAction> Actions { get; private set; } = [];
        public void SetActions(IReadOnlyList<NativeNotificationAction> actions) => Actions = actions;
        public void Show() { Dispatcher.UIThread.VerifyAccess(); manager.Shows++; manager.Active[Id] = this; }
        public void Close() { Dispatcher.UIThread.VerifyAccess(); manager.Active.Remove(Id); }
    }

    internal static PhoneNotification Existing { get; } = new() { Key = "existing", PackageName = "test.app0", Title = "已有通知", Text = "基线", Clearable = true };

    internal static async Task RunAsync(MainViewModel model, MainWindow window, MockPhone phone, Manager manager, string temporary, CancellationToken ct)
    {
        await NotificationReplyChecks.RunAsync(ct);
        await LinuxNotificationChecks.RunAsync(ct);
        Program.Check(JsonSerializer.Deserialize<Settings>("{}")!.NotificationSyncEnabled, "legacy settings default notification sync to enabled");
        window.ShowSettings();
        window.UpdateLayout();
        var toggle = window.FindControl<SettingsView>("SettingsPage")!.FindControl<ToggleSwitch>("NotificationSyncToggle")!;
        Program.Check(toggle.IsChecked == true && manager.Created.Count == 0, "settings exposes notification sync and initial snapshot does not replay old alerts");

        PhoneNotification record = Existing with { Key = "0|test.app0|42|tag=消息;|0", Title = "新消息 <&> \"✓\"", Text = "合成通知\nUnicode ✓" };
        await phone.PushNotificationsAsync(Existing, record);
        await UntilAsync(() => manager.Active.Count == 1, ct);
        Toast first = manager.Created.Last();
        Program.Check(first.Title!.Contains("新消息", StringComparison.Ordinal) && first.Message == record.Text && first.Actions.Count == 2 && first.Tag != record.Key,
            "new phone notifications become native toasts with open/dismiss actions and safe activation tags");
        int count = manager.Created.Count;
        int shows = manager.Shows;
        await phone.PushNotificationsAsync(Existing, record, record);
        await BarrierAsync(model, ct);
        Program.Check(manager.Created.Count == count && manager.Shows == shows, "unchanged snapshots and duplicate keys do not repeat notifications");
        record = record with { PostTime = 10, UpdateTime = 11, SubText = "不可见元数据", BigText = "普通正文存在时不显示的展开正文" };
        await phone.PushNotificationsAsync(Existing, record);
        await BarrierAsync(model, ct);
        Program.Check(manager.Shows == shows, "timestamp and undisplayed metadata changes do not resend identical native notifications");
        record = record with { Text = "更新后的内容", UpdateTime = 5 };
        await phone.PushNotificationsAsync(Existing, record);
        await UntilAsync(() => manager.Shows == shows + 1, ct);
        Program.Check(manager.Active.Count == 1 && manager.Active.ContainsKey(first.Id) && first.Message == record.Text,
            "updates reuse the native notification ID and replace its content without duplicate toasts");

        await manager.CompleteAsync(manager.Created.Last().Id, "dismiss");
        await UntilAsync(() => phone.NotificationRequests.Any(r => Command(r, 2)), ct);
        Program.Check(phone.NotificationRequests.Last(r => Command(r, 2)).GetProperty("key").GetString() == record.Key,
            "native callback from a worker thread clears the matching phone notification");
        await UntilAsync(() => manager.Active.Count == 0, ct);

        record = record with { Key = "open", Text = "点击打开", UpdateTime = 6 };
        await phone.PushNotificationsAsync(Existing, record);
        await UntilAsync(() => manager.Active.Count == 1, ct);
        await manager.CompleteAsync(manager.Created.Last().Id, activated: true);
        await UntilAsync(() => phone.NotificationRequests.Any(r => Command(r, 6)), ct);
        var open = phone.NotificationRequests.Last(r => Command(r, 6));
        Program.Check(model.Mirrors.Any(m => m.SessionId == open.GetProperty("sessionId").GetString()) && open.GetProperty("fallbackUserId").GetInt32() == 0,
            "clicking a native notification opens an application mirror and forwards the typed notification intent");
        await UntilAsync(() => manager.Active.Count == 0, ct);

        record = record with { Key = "withdraw", Clearable = false };
        await phone.PushNotificationsAsync(Existing, record);
        await UntilAsync(() => manager.Active.Count == 1, ct);
        Program.Check(manager.Created.Last().Actions.All(a => a.Tag != "dismiss"), "ongoing notifications do not expose a phone dismiss action");
        await phone.PushNotificationsAsync(Existing);
        await UntilAsync(() => manager.Active.Count == 0, ct);
        Program.Check(true, "phone withdrawal removes its native notification");

        manager.Available = false;
        await phone.PushNotificationsAsync(Existing, record with { Key = "native-starting" });
        await BarrierAsync(model, ct);
        manager.Available = true;
        await UntilAsync(() => manager.Active.Count == 1, ct);
        Program.Check(true, "notifications retry while the native notification manager initializes");

        toggle.IsChecked = false;
        Program.Check(manager.Active.Count == 0, "disabling sync immediately closes native notifications");
        await UntilAsync(() => phone.NotificationRequests.Last().TryGetProperty("on", out var on) && !on.GetBoolean(), ct);
        count = manager.Created.Count;
        await phone.PushNotificationsAsync(record with { Key = "late" });
        await BarrierAsync(model, ct);
        Program.Check(manager.Created.Count == count && !JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path.Combine(temporary, "settings.json")))!.NotificationSyncEnabled,
            "disabled notifications persist and late snapshots cannot produce native toasts");

        // Re-enable while the disable request is still awaiting its response.
        int subscriptions = phone.NotificationRequests.Count(r => r.TryGetProperty("on", out _));
        phone.NotificationSubscriptionGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        toggle.IsChecked = true;
        await UntilAsync(() => phone.NotificationRequests.Last().TryGetProperty("on", out var on) && on.GetBoolean(), ct);
        toggle.IsChecked = false;
        toggle.IsChecked = true;
        phone.NotificationSubscriptionGate.SetResult();
        phone.NotificationSubscriptionGate = null;
        await UntilAsync(() => phone.NotificationRequests.Count(r => r.TryGetProperty("on", out _)) >= subscriptions + 3 &&
            phone.NotificationRequests.Last().GetProperty("on").GetBoolean(), ct);
        await BarrierAsync(model, ct);
        await phone.PushNotificationsAsync(record with { Key = "after-toggle" });
        await UntilAsync(() => manager.Active.Count == 1, ct);
        Program.Check(true, "rapid off/on during subscription settles on a live enabled subscription");
        uint staleId = manager.Created.Last().Id;
        await model.DisconnectCommand.Execute().ToTask();
        int commands = phone.NotificationRequests.Count(r => r.TryGetProperty("command", out _));
        await manager.CompleteAsync(staleId, "open");
        await Task.Delay(50, ct);
        Program.Check(manager.Active.Count == 0 && commands == phone.NotificationRequests.Count(r => r.TryGetProperty("command", out _)),
            "disconnect clears notifications and stale native callbacks cannot operate on a retired device");

        phone.SupportsNotifications = false;
        int requests = phone.NotificationRequests.Count;
        await model.ConnectCommand.Execute().ToTask();
        Program.Check(phone.NotificationRequests.Count == requests, "devices without the notification capability are never subscribed");
        await model.DisconnectCommand.Execute().ToTask();

        phone.SupportsNotifications = true;
        phone.NotificationSubscriptionError = "no-permission";
        await model.ConnectCommand.Execute().ToTask();
        Program.Check(model.IsConnected && model.Error.Contains("no-permission", StringComparison.Ordinal) && manager.Active.Count == 0,
            "Android permission failure is visible without breaking the device connection");
    }

    private static bool Command(JsonElement request, int command) => request.TryGetProperty("command", out var value) && value.GetInt32() == command;
    private static async Task UntilAsync(Func<bool> condition, CancellationToken ct)
    {
        while (!condition()) await Task.Delay(10, ct);
    }
    private static async Task BarrierAsync(MainViewModel model, CancellationToken ct)
    {
        // device-info travels after the pushed snapshot on the same control reader.
        await model.ShowDeviceCommand.Execute().ToTask().WaitAsync(ct);
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
    }
}
