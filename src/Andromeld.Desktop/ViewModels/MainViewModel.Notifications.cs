using Andromeld.Desktop.Services;
using Andromeld.Protocol;

using Avalonia.Labs.Notifications;

using ReactiveUI.SourceGenerators;

namespace Andromeld.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private readonly INativeNotificationManager? _notificationManager;
    private NotificationSharing? _notificationSharing;

    [Reactive]
    public partial bool NotificationSyncEnabled { get; set; }

    private async Task StartNotificationsAsync()
    {
        if (_notificationSharing is not null || _notificationManager is null || !NotificationSyncEnabled ||
            !IsConnected || _client is not { } client || !client.Capabilities.Contains("notification")) return;

        NotificationSharing sharing = new(client, _notificationManager, (record, ct) => GetNotificationSessionAsync(client, record, ct),
            OperatingSystem.IsWindows(), ex => Error = $"通知同步失败：{ex.Message}", _connection.Token);
        _notificationSharing = sharing;
        try { await sharing.StartAsync().ConfigureAwait(true); }
        catch (Exception ex)
        {
            await StopNotificationsAsync().ConfigureAwait(true);
            if (ex is not OperationCanceledException) Error = $"通知同步失败：{ex.Message}";
        }
    }

    private async Task StopNotificationsAsync()
    {
        NotificationSharing? sharing = _notificationSharing;
        _notificationSharing = null;
        if (sharing is not null) await sharing.DisposeAsync().ConfigureAwait(true);
    }

    private async Task<string> GetNotificationSessionAsync(AndromeldClient client, PhoneNotification record, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_disposed || !IsConnected || _client != client) throw new IOException("设备连接已断开。");
        PhoneApp app = _allApps.Find(tile => tile.App.UserId == record.UserId &&
            string.Equals(tile.Package, record.PackageName, StringComparison.Ordinal))?.App ?? new PhoneApp(record.PackageName, record.PackageName, record.UserId, false, -1, false);
        await OpenMirrorAsync(app).ConfigureAwait(true);
        ct.ThrowIfCancellationRequested();
        if (_disposed || _client != client || !IsConnected) throw new IOException("设备连接已断开。");
        return Mirrors.FirstOrDefault(m => !m.IsDisposed && m.CanControl && m.App?.UserId == record.UserId &&
            string.Equals(m.App?.PackageName, record.PackageName, StringComparison.Ordinal))?.SessionId
            ?? throw new IOException("无法打开通知对应的应用镜像。");
    }
}
