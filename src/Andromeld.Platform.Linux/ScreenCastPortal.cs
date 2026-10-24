using System.Diagnostics;

using Microsoft.Win32.SafeHandles;

using Tmds.DBus.Protocol;

namespace Andromeld.Platform.Linux;

/// <summary>A single authorized screen/window stream. The caller serializes setup and disposal;
/// D-Bus callbacks only complete task sources and never access the caller's mutable state.</summary>
public sealed class ScreenCastPortal : IAsyncDisposable
{
    private const string Service = "org.freedesktop.portal.Desktop";
    private const string DesktopPath = "/org/freedesktop/portal/desktop";
    private const string ScreenCast = "org.freedesktop.portal.ScreenCast";
    private readonly DBusConnection _connection;
    private readonly string _session;
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IDisposable? _closedSubscription;
    private SafeFileHandle? _remote;
    private bool _disposed;

    private ScreenCastPortal(DBusConnection connection, string session)
    {
        _connection = connection;
        _session = session;
    }

    public uint NodeId { get; private set; }
    public ulong? Serial { get; private set; }
    public SafeFileHandle Remote => _remote ?? throw new InvalidOperationException("屏幕共享尚未连接。");
    public Task Closed => _closed.Task;

    public static async Task<ScreenCastPortal> OpenAsync(string parentWindow, CancellationToken ct, string? busAddress = null)
    {
        var connection = new DBusConnection(busAddress ?? DBusAddress.Session ?? throw new IOException("无法连接桌面会话 D-Bus。"));
        ScreenCastPortal? portal = null;
        try
        {
            await connection.ConnectAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            var types = await connection.CallMethodAsync(SourceTypesMessage(connection),
                static (message, _) => message.GetBodyReader().ReadVariantValue().GetUInt32()).WaitAsync(ct).ConfigureAwait(false) & 3u;
            if (types == 0) throw new IOException("桌面 Portal 不支持屏幕或窗口共享。");
            var token = "andromeld_" + Guid.NewGuid().ToString("N");
            var sender = (connection.UniqueName ?? throw new IOException("D-Bus 未返回连接名。" )).TrimStart(':').Replace('.', '_');
            // The predictable session path also lets cancellation close a session whose response is still in flight.
            portal = new(connection, $"/org/freedesktop/portal/desktop/session/{sender}/{token}");
            var created = await portal.RequestAsync("CreateSession", null, null,
                new(StringComparer.Ordinal) { ["session_handle_token"] = token }, ct).ConfigureAwait(false);
            if (!string.Equals(created["session_handle"].GetString(), portal._session, StringComparison.Ordinal))
                throw new IOException("屏幕共享返回了意外的会话路径。");

            portal._closedSubscription = await connection.AddMatchAsync(
                new MatchRule { Type = MessageType.Signal, Sender = Service, Path = portal._session, Interface = "org.freedesktop.portal.Session", Member = "Closed" },
                static (_, _) => true,
                static notification => ((TaskCompletionSource)notification.State!).TrySetResult(),
                emitOnCapturedContext: false, state: portal._closed).ConfigureAwait(false);
            await portal.RequestAsync("SelectSources", portal._session, null,
                new(StringComparer.Ordinal) { ["types"] = types, ["multiple"] = false }, ct).ConfigureAwait(false);
            var started = await portal.RequestAsync("Start", portal._session, parentWindow, new(StringComparer.Ordinal), ct).ConfigureAwait(false);
            var streams = started["streams"];
            if (streams.Count != 1) throw new IOException("请选择一个屏幕或窗口。");
            var stream = streams.GetItem(0);
            portal.NodeId = stream.GetItem(0).GetUInt32();
            var properties = stream.GetItem(1).GetDictionary<string, VariantValue>();
            if (properties.TryGetValue("pipewire-serial", out var serial)) portal.Serial = serial.GetUInt64();
            // Abort the call itself: abandoning a task which later receives an FD would leak that FD.
            using var abort = ct.UnsafeRegister(static state => ((DBusConnection)state!).Dispose(), connection);
            portal._remote = await connection.CallMethodAsync(portal.OpenRemoteMessage(),
                static (message, _) => message.GetBodyReader().ReadHandle<SafeFileHandle>()).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return portal;
        }
        catch
        {
            if (portal is not null) await portal.DisposeAsync().ConfigureAwait(false);
            else connection.Dispose();
            throw;
        }
    }

    private async Task<Dictionary<string, VariantValue>> RequestAsync(string member, string? session, string? parent, Dictionary<string, VariantValue> options, CancellationToken ct)
    {
        var token = "andromeld_" + Guid.NewGuid().ToString("N");
        var sender = (_connection.UniqueName ?? throw new IOException("D-Bus 已断开。" )).TrimStart(':').Replace('.', '_');
        var path = $"/org/freedesktop/portal/desktop/request/{sender}/{token}";
        options["handle_token"] = token;
        var completion = new TaskCompletionSource<PortalResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = await _connection.AddMatchAsync(
            new MatchRule { Type = MessageType.Signal, Sender = Service, Path = path, Interface = "org.freedesktop.portal.Request", Member = "Response" },
            static (message, _) =>
            {
                var reader = message.GetBodyReader();
                return new PortalResponse(reader.ReadUInt32(), reader.ReadDictionaryOfStringToVariantValue());
            },
            static notification =>
            {
                var target = (TaskCompletionSource<PortalResponse>)notification.State!;
                if (notification.HasValue) target.TrySetResult(notification.Value);
                else target.TrySetException(notification.Exception ?? new IOException("屏幕共享 D-Bus 连接已关闭。"));
            }, emitOnCapturedContext: false, state: completion).ConfigureAwait(false);
        try
        {
            var returned = await _connection.CallMethodAsync(RequestMessage(member, session, parent, options),
                static (message, _) => message.GetBodyReader().ReadObjectPath().ToString()).WaitAsync(ct).ConfigureAwait(false);
            if (!string.Equals(returned, path, StringComparison.Ordinal)) throw new IOException("屏幕共享返回了意外的请求路径。");
            var finished = await Task.WhenAny(completion.Task, _closed.Task).WaitAsync(ct).ConfigureAwait(false);
            if (finished == _closed.Task) throw new IOException("系统已停止屏幕共享。");
            var response = await completion.Task.ConfigureAwait(false);
            if (response.Code == 1) throw new OperationCanceledException("已取消系统屏幕共享。", ct);
            if (response.Code != 0) throw new IOException("系统未能启动屏幕共享，请检查桌面 Portal 服务。");
            return response.Results;
        }
        finally
        {
            if (!completion.Task.IsCompletedSuccessfully)
                await CloseAsync(path, "org.freedesktop.portal.Request").ConfigureAwait(false);
        }
    }

    private MessageBuffer RequestMessage(string member, string? session, string? parent, Dictionary<string, VariantValue> options)
    {
        using var writer = _connection.GetMessageWriter();
        var signature = session is null ? "a{sv}" : "oa{sv}";
        if (parent is not null) signature = "osa{sv}";
        writer.WriteMethodCallHeader(destination: Service, path: DesktopPath, @interface: ScreenCast, member: member, signature: signature);
        if (session is not null) writer.WriteObjectPath(session);
        if (parent is not null) writer.WriteString(parent);
        writer.WriteDictionary(options);
        return writer.CreateMessage();
    }

    private MessageBuffer OpenRemoteMessage()
    {
        using var writer = _connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: Service, path: DesktopPath, @interface: ScreenCast, member: "OpenPipeWireRemote", signature: "oa{sv}");
        writer.WriteObjectPath(_session);
        writer.WriteDictionary(new Dictionary<string, VariantValue>(StringComparer.Ordinal));
        return writer.CreateMessage();
    }

    private static MessageBuffer SourceTypesMessage(DBusConnection connection)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: Service, path: DesktopPath, @interface: "org.freedesktop.DBus.Properties", member: "Get", signature: "ss");
        writer.WriteString(ScreenCast);
        writer.WriteString("AvailableSourceTypes");
        return writer.CreateMessage();
    }

    private async Task CloseAsync(string path, string @interface)
    {
        try
        {
            await _connection.CallMethodAsync(CloseMessage(path, @interface)).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception ex) { Debug.WriteLine(ex.Message); }
    }

    private MessageBuffer CloseMessage(string path, string @interface)
    {
        using var writer = _connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: Service, path: path, @interface: @interface, member: "Close");
        return writer.CreateMessage();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _remote?.Dispose();
        _remote = null;
        _closedSubscription?.Dispose();
        _closedSubscription = null;
        await CloseAsync(_session, "org.freedesktop.portal.Session").ConfigureAwait(false);
        _connection.Dispose();
        _closed.TrySetResult();
    }

    private sealed record PortalResponse(uint Code, Dictionary<string, VariantValue> Results);
}
