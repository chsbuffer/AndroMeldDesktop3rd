using System.Globalization;
using System.Net.WebSockets;
using System.Security;

using Andromeld.Protocol;

using Avalonia.Labs.Notifications;
using Avalonia.Threading;

namespace Andromeld.Desktop.Services;

/// <summary>UI-thread owned. The reader and native callbacks only enqueue work on the dispatcher.</summary>
public sealed class NotificationSharing : IAsyncDisposable
{
    private readonly AndromeldClient _client;
    private readonly INativeNotificationManager _manager;
    private readonly Func<PhoneNotification, CancellationToken, Task<string>> _getSession;
    private readonly bool _supportsInlineReply;
    private readonly Action<Exception> _onError;
    private readonly CancellationTokenSource _stop;
    private readonly CancellationToken _token;
    private readonly Dictionary<string, PhoneNotification> _known = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PhoneNotification> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, INativeNotification> _shown = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _retry = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _hasSnapshot, _disposed, _released;
    private int _retryCount;
    private Task _action = Task.CompletedTask;

    public NotificationSharing(AndromeldClient client, INativeNotificationManager manager,
        Func<PhoneNotification, CancellationToken, Task<string>> getSession, bool supportsInlineReply, Action<Exception> onError, CancellationToken connection)
    {
        Dispatcher.UIThread.VerifyAccess();
        _client = client;
        _manager = manager;
        _getSession = getSession;
        _supportsInlineReply = supportsInlineReply;
        _onError = onError;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(connection);
        _token = _stop.Token;
        _client.NotificationsReceived += Receive;
        _manager.NotificationCompleted += Completed;
        if (_manager is LinuxNotificationManager linux) linux.Failed += NativeFailed;
        _retry.Tick += Retry;
    }

    public Task StartAsync() => _client.SetNotificationsEnabledAsync(true, _token);

    public bool IsStopped => _disposed;

    public void Deactivate()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();
        _retry.Stop();
        _retry.Tick -= Retry;
        _client.NotificationsReceived -= Receive;
        _manager.NotificationCompleted -= Completed;
        if (_manager is LinuxNotificationManager linux) linux.Failed -= NativeFailed;
        foreach (INativeNotification notification in _shown.Values) Close(notification);
        _shown.Clear();
        _pending.Clear();
        _known.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        if (_released) return;
        _released = true;
        Deactivate();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
        try { await _client.SetNotificationsEnabledAsync(false, timeout.Token).ConfigureAwait(true); }
        catch (Exception ex) when (ex is IOException or WebSocketException or OperationCanceledException or ObjectDisposedException or TimeoutException)
        {
            // Disconnect may already have closed the shared control socket.
        }

        if (_manager is LinuxNotificationManager linux)
        {
            try { await linux.Pending.WaitAsync(timeout.Token).ConfigureAwait(true); }
            catch (OperationCanceledException) { /* Bound native withdrawal during shutdown. */ }
        }

        // An activation can be awaiting a mirror whose startup joins the sharing pump.
        // Its captured token and the VM's client guard retire that continuation without a lifecycle cycle.
        _stop.Dispose();
    }

    private void Receive(NotificationSnapshot snapshot) => Dispatcher.UIThread.Post(() => Apply(snapshot));

    private void NativeFailed(Exception ex)
    {
        if (!_disposed) _onError(ex);
    }

    private void Apply(NotificationSnapshot snapshot)
    {
        if (_disposed || _token.IsCancellationRequested) return;
        HashSet<string> active = new(StringComparer.Ordinal);
        foreach (PhoneNotification record in snapshot.Active)
        {
            if (!active.Add(record.Key)) continue;
            if (_hasSnapshot && (_pending.ContainsKey(record.Key) || !_known.TryGetValue(record.Key, out PhoneNotification? previous) || !SamePresentation(previous, record)))
            {
                _pending[record.Key] = record;
            }

            _known[record.Key] = record;
        }

        foreach (string key in _known.Keys.Where(key => !active.Contains(key)).ToArray())
        {
            _known.Remove(key);
            _pending.Remove(key);
            if (_shown.Remove(key, out INativeNotification? notification)) Close(notification);
        }

        // Match the Web client: the initial snapshot establishes a baseline without replaying old alerts.
        _hasSnapshot = true;
        _retryCount = 0;
        PresentPending();
    }

    private void PresentPending()
    {
        foreach ((string key, PhoneNotification record) in _pending.ToArray())
        {
            try
            {
                if ((_shown.GetValueOrDefault(key) ?? _manager.CreateNotification(null)) is not { } notification) break;
                notification.Title = NativeText(NotificationTitle(record));
                notification.Message = NativeText(NotificationMessage(record));
                List<NativeNotificationAction> actions = [new("打开", "open")];
                if (record.Clearable) actions.Add(new NativeNotificationAction("清除", "dismiss"));
                notification.ReplyActionTag = null;
                if (ReplyAction(record) is { } reply)
                {
                    notification.ReplyActionTag = ReplyTag(reply.Index);
                    actions.Insert(0, new NativeNotificationAction(NativeText(string.IsNullOrEmpty(reply.Title) ? "回复" : reply.Title) ?? "回复", notification.ReplyActionTag));
                }

                notification.SetActions(actions);
                // Windows embeds Tag in an XML attribute and parses it as activation arguments.
                notification.Tag = "andromeld";
                notification.Show();
                _shown[key] = notification;
            }
            catch (Exception ex)
            {
                _onError(ex);
            }

            _pending.Remove(key);
        }

        if (_pending.Count == 0) _retry.Stop();
        else _retry.Start();
    }

    private static string? NativeText(string? text) => OperatingSystem.IsWindows() ? SecurityElement.Escape(text) : text;

    private static string ReplyTag(int index) => "reply-" + index.ToString(CultureInfo.InvariantCulture);

    private static string NotificationTitle(PhoneNotification record) => string.IsNullOrEmpty(record.Title) ? record.PackageName : record.Title;

    private static string NotificationMessage(PhoneNotification record) => (string.IsNullOrEmpty(record.Text) ? record.BigText : record.Text) ?? "";

    private PhoneNotificationAction? ReplyAction(PhoneNotification record) =>
        _supportsInlineReply ? record.Actions.FirstOrDefault(static action => action.HasRemoteInput) : null;

    private bool SamePresentation(PhoneNotification left, PhoneNotification right) =>
        left.Clearable == right.Clearable && ReplyAction(left) == ReplyAction(right) &&
        string.Equals(NotificationTitle(left), NotificationTitle(right), StringComparison.Ordinal) &&
        string.Equals(NotificationMessage(left), NotificationMessage(right), StringComparison.Ordinal);

    private void Retry(object? sender, EventArgs e)
    {
        if (++_retryCount < 10)
        {
            PresentPending();
            return;
        }

        _retry.Stop();
        _pending.Clear();
        _onError(new IOException("系统通知服务不可用，请检查桌面的通知权限和通知服务。"));
    }

    private void Completed(object? sender, NativeNotificationCompletedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed || _token.IsCancellationRequested || !_action.IsCompleted || e.NotificationId is not { } id) return;
            string? key = _shown.FirstOrDefault(pair => pair.Value.Id == id).Key;
            if (key is not null && _known.TryGetValue(key, out PhoneNotification? record))
            {
                _action = ActAsync(record, e);
            }
        });
    }

    private async Task ActAsync(PhoneNotification record, NativeNotificationCompletedEventArgs e)
    {
        try
        {
            if (e.ActionTag?.StartsWith("reply-", StringComparison.Ordinal) == true)
            {
                if (!_supportsInlineReply || e.UserData is not string text || string.IsNullOrWhiteSpace(text) ||
                    !int.TryParse(e.ActionTag.AsSpan(6), NumberStyles.None, CultureInfo.InvariantCulture, out int index) ||
                    record.Actions.FirstOrDefault(static action => action.HasRemoteInput) is not { } reply || reply.Index != index) return;

                // The helper requires a mirror session for RemoteInput, including broadcast/service actions.
                string sessionId = await _getSession(record, _token).ConfigureAwait(true);
                _token.ThrowIfCancellationRequested();
                if (!_known.TryGetValue(record.Key, out PhoneNotification? latest) || latest.Actions.FirstOrDefault(static action => action.HasRemoteInput) != reply ||
                    !_shown.ContainsKey(record.Key)) return;
                await _client.ReplyToNotificationAsync(record.Key, reply.Index, text, sessionId, _token).ConfigureAwait(true);
            }
            else if (e.ActionTag is "dismiss" || e.IsCancelled)
            {
                if (record.Clearable) await _client.DismissNotificationAsync(record.Key, _token).ConfigureAwait(true);
            }
            else if (e.IsActivated || e.ActionTag is "open" or "default")
            {
                string? sessionId = record.ContentIntentTargetKind is "activity" or "unknown" or "none"
                    ? await _getSession(record, _token).ConfigureAwait(true) : null;
                _token.ThrowIfCancellationRequested();
                await _client.OpenNotificationAsync(record, sessionId, _token).ConfigureAwait(true);
            }
            else return;

            if (!_disposed && _known.TryGetValue(record.Key, out PhoneNotification? current) && SamePresentation(current, record) &&
                _shown.TryGetValue(record.Key, out INativeNotification? notification) && notification.Id == e.NotificationId)
            {
                _shown.Remove(record.Key);
                Close(notification);
            }
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested)
        {
            // A disconnected/disabled subscription no longer owns activation work.
        }
        catch (Exception ex)
        {
            if (!_disposed) _onError(ex);
        }
    }

    private void Close(INativeNotification notification)
    {
        try { notification.Close(); }
        catch (Exception ex) { _onError(ex); }
    }
}
