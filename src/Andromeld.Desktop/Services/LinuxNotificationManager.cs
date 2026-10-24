using System.Security;

using Avalonia.Labs.Notifications;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

using Tmds.DBus.Protocol;

namespace Andromeld.Desktop.Services;

/// <summary>
/// UI-owned Labs notification backend for Avalonia 12.1's D-Bus ABI. Labs 12.0.2's built-in
/// Linux backend references the removed Tmds Connection type (0.92); Avalonia requires 0.94.1.
/// Uses the freedesktop notification service so unpackaged apps need no portal desktop ID.
/// </summary>
internal sealed class LinuxNotificationManager : INativeNotificationManager, IDisposable
{
    private const string Service = "org.freedesktop.Notifications";
    private const string ObjectPath = "/org/freedesktop/Notifications";
    private const string RateLimitError = "org.freedesktop.Notifications.Error.ExcessNotificationGeneration";
    // Plasma rejects identical payloads less than a second apart, including replacements.
    private const int NotifyIntervalMilliseconds = 1100;
    private static readonly string[] DefaultAction = ["default", "打开"];
    private readonly DBusConnection _connection;
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<uint, INativeNotification> _active = [];
    private readonly Dictionary<uint, uint> _serverIds = [];
    private readonly HashSet<uint> _scheduledShows = [];
    private IDisposable? _actions, _closed;
    private Task _operation;
    private long _nextNotifyAt;
    private uint _nextId;
    private bool _ready, _disposed;

    internal LinuxNotificationManager(string? busAddress = null)
    {
        Dispatcher.UIThread.VerifyAccess();
        _connection = new DBusConnection(busAddress ?? DBusAddress.Session ?? throw new IOException("未找到桌面 D-Bus 会话。"));
        _operation = InitializeAsync();
    }

    public IReadOnlyDictionary<uint, INativeNotification> ActiveNotifications => _active;
    public event EventHandler<NativeNotificationCompletedEventArgs>? NotificationCompleted;
    internal event Action<Exception>? Failed;
    internal Exception? LastError { get; private set; }
    internal Task Pending => DrainAsync();

    public INativeNotification? CreateNotification(string? category)
    {
        Dispatcher.UIThread.VerifyAccess();
        return _ready && !_disposed ? new LinuxNotification(++_nextId, category ?? "default", this) : null;
    }

    public void CloseAll()
    {
        Dispatcher.UIThread.VerifyAccess();
        foreach (INativeNotification notification in _active.Values.ToArray()) notification.Close();
    }

    public void Dispose()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();
        _actions?.Dispose();
        _closed?.Dispose();
        _connection.Dispose();
        _active.Clear();
        _serverIds.Clear();
        _scheduledShows.Clear();
        _stop.Dispose();
    }

    private async Task InitializeAsync()
    {
        try
        {
            await _connection.ConnectAsync().ConfigureAwait(true);
            _actions = await _connection.AddMatchAsync(new MatchRule { Type = MessageType.Signal, Sender = Service, Path = ObjectPath, Interface = Service, Member = "ActionInvoked" },
                static (message, _) => { var reader = message.GetBodyReader(); return (Id: reader.ReadUInt32(), Action: reader.ReadString()); },
                signal =>
                {
                    if (signal.HasValue) Dispatcher.UIThread.Post(() => Complete(signal.Value.Id, signal.Value.Action));
                }, emitOnCapturedContext: false).ConfigureAwait(true);
            _closed = await _connection.AddMatchAsync(new MatchRule { Type = MessageType.Signal, Sender = Service, Path = ObjectPath, Interface = Service, Member = "NotificationClosed" },
                static (message, _) => { var reader = message.GetBodyReader(); return (Id: reader.ReadUInt32(), Reason: reader.ReadUInt32()); },
                signal =>
                {
                    if (signal.HasValue) Dispatcher.UIThread.Post(() => Closed(signal.Value.Id, signal.Value.Reason));
                }, emitOnCapturedContext: false).ConfigureAwait(true);
            if (!_disposed) _ready = true;
        }
        catch (Exception ex) { Report(ex); }
    }

    private void Show(LinuxNotification notification)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_disposed) return;
        _active[notification.Id] = notification;
        notification.Revision++;
        ScheduleShow(notification);
    }

    private void ScheduleShow(LinuxNotification notification)
    {
        if (!_scheduledShows.Add(notification.Id)) return;
        _operation = RunAsync(_operation, () => SendQueuedAsync(notification));
    }

    private async Task SendQueuedAsync(LinuxNotification notification)
    {
        long attemptedRevision = notification.Revision;
        try
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (_disposed || !_active.ContainsKey(notification.Id)) return;
                long delay = _nextNotifyAt - Environment.TickCount64;
                if (delay > 0) await Task.Delay(TimeSpan.FromMilliseconds(delay), _stop.Token).ConfigureAwait(true);
                if (_disposed || !_active.ContainsKey(notification.Id)) return;
                attemptedRevision = notification.Revision;
                _nextNotifyAt = Environment.TickCount64 + NotifyIntervalMilliseconds;
                try
                {
                    uint id = await _connection.CallMethodAsync(NotifyMessage(notification), static (message, _) => message.GetBodyReader().ReadUInt32()).ConfigureAwait(true);
                    if (_disposed) return;
                    _serverIds[notification.Id] = id;
                    LastError = null;
                    return;
                }
                catch (DBusErrorReplyException ex) when (string.Equals(ex.ErrorName, RateLimitError, StringComparison.Ordinal))
                {
                    if (attempt == 2) throw new IOException("系统通知服务持续限流，请稍后重试。", ex);
                    _nextNotifyAt = Environment.TickCount64 + NotifyIntervalMilliseconds;
                }
            }
        }
        finally
        {
            _scheduledShows.Remove(notification.Id);
            // Updates received while Notify was in flight need one more send with the newest state.
            if (!_disposed && _active.ContainsKey(notification.Id) && notification.Revision != attemptedRevision) ScheduleShow(notification);
        }
    }

    private async Task DrainAsync()
    {
        Task operation;
        do
        {
            operation = _operation;
            await operation.ConfigureAwait(true);
        } while (!ReferenceEquals(operation, _operation));
    }

    private MessageBuffer NotifyMessage(LinuxNotification notification)
    {
        using MessageWriter writer = _connection.GetMessageWriter();
        writer.WriteMethodCallHeader(Service, ObjectPath, Service, "Notify", "susssasa{sv}i");
        writer.WriteString("AndroMeld Desktop");
        writer.WriteUInt32(_serverIds.GetValueOrDefault(notification.Id));
        writer.WriteString("");
        writer.WriteString(notification.Title ?? "");
        writer.WriteString(SecurityElement.Escape(notification.Message) ?? "");
        writer.WriteArray(DefaultAction.Concat(notification.Actions.SelectMany(static action => new[] { action.Tag, action.Caption })).ToArray());
        writer.WriteDictionary(new Dictionary<string, VariantValue>(StringComparer.Ordinal));
        writer.WriteInt32(notification.Expiration is { } expiration ? (int)Math.Clamp(expiration.TotalMilliseconds, 0, int.MaxValue) : -1);
        return writer.CreateMessage();
    }

    private void Close(LinuxNotification notification)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_disposed || !_active.Remove(notification.Id)) return;
        _operation = RunAsync(_operation, async () =>
        {
            if (!_serverIds.Remove(notification.Id, out uint id)) return;
            MessageBuffer message;
            using (MessageWriter writer = _connection.GetMessageWriter())
            {
                writer.WriteMethodCallHeader(Service, ObjectPath, Service, "CloseNotification", "u");
                writer.WriteUInt32(id);
                message = writer.CreateMessage();
            }

            await _connection.CallMethodAsync(message).ConfigureAwait(true);
        });
    }

    private async Task RunAsync(Task previous, Func<Task> operation)
    {
        await previous.ConfigureAwait(true);
        if (_disposed) return;
        try { await operation().ConfigureAwait(true); }
        catch (Exception ex) { Report(ex); }
    }

    private void Report(Exception ex)
    {
        if (_disposed) return;
        LastError = ex;
        Failed?.Invoke(ex);
    }

    private void Complete(uint serverId, string action)
    {
        if (_disposed) return;
        uint id = _serverIds.FirstOrDefault(pair => pair.Value == serverId).Key;
        if (_active.ContainsKey(id)) NotificationCompleted?.Invoke(this, new NativeNotificationCompletedEventArgs
        {
            NotificationId = id, IsActivated = string.Equals(action, "default", StringComparison.Ordinal), ActionTag = action
        });
    }

    private void Closed(uint serverId, uint reason)
    {
        if (_disposed) return;
        uint id = _serverIds.FirstOrDefault(pair => pair.Value == serverId).Key;
        _serverIds.Remove(id);
        if (_active.Remove(id) && reason == 2) NotificationCompleted?.Invoke(this, new NativeNotificationCompletedEventArgs { NotificationId = id, IsCancelled = true });
    }

    private sealed class LinuxNotification(uint id, string category, LinuxNotificationManager manager) : INativeNotification
    {
        internal long Revision { get; set; }
        public uint Id => id;
        public string Category => category;
        public string? Title { get; set; }
        public string? Tag { get; set; }
        public string? Message { get; set; }
        public TimeSpan? Expiration { get; set; }
        public Bitmap? Icon { get; set; }
        public string? ReplyActionTag { get; set; }
        public IReadOnlyList<NativeNotificationAction> Actions { get; private set; } = [];
        public void SetActions(IReadOnlyList<NativeNotificationAction> actions) => Actions = actions;
        public void Show() => manager.Show(this);
        public void Close() => manager.Close(this);
    }
}
