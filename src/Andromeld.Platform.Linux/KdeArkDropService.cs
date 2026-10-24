using System.Diagnostics;

using Tmds.DBus.Protocol;

namespace Andromeld.Platform.Linux;

/// <summary>
/// Exposes the KIO/Ark drop callback on a temporary object path. D-Bus reads the call and posts it to the
/// injected owner context; all service state belongs to that context, so no lock is needed.
/// </summary>
public sealed class KdeArkDropService : IPathMethodHandler, IDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DestinationTimeout = TimeSpan.FromSeconds(5);
    private static readonly ReadOnlyMemory<byte> InterfaceXml = """
        <interface name="org.kde.ark.DndExtract">
          <method name="extractSelectedFilesTo"><arg direction="in" type="s" name="destination"/></method>
        </interface>
        """u8.ToArray();
    private readonly DBusConnection _connection;
    private readonly SynchronizationContext _owner;
    private readonly TaskCompletionSource<string> _destination = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;
    private bool _received;

    private KdeArkDropService(DBusConnection connection, SynchronizationContext owner, string serviceName)
    {
        _connection = connection;
        _owner = owner;
        ServiceName = serviceName;
        ObjectPath = "/org/andromeld/filedrag/" + Guid.NewGuid().ToString("N");
        _connection.AddMethodHandler(this);
    }

    public string ServiceName { get; }
    public string ObjectPath { get; }
    public bool HandlesChildPaths => false;
    string IPathMethodHandler.Path => ObjectPath;

    /// <summary>Connects the temporary session-bus object, or returns <see langword="null"/> when KDE is unavailable.</summary>
    public static async Task<KdeArkDropService?> TryCreateAsync(SynchronizationContext owner, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (!OperatingSystem.IsLinux() || !KdeSession.IsKde || DBusAddress.Session is not { } address) return null;
        var connection = new DBusConnection(address);
        try
        {
            await connection.ConnectAsync().AsTask().WaitAsync(ConnectTimeout, ct).ConfigureAwait(false);
            string serviceName = connection.UniqueName ?? throw new IOException("D-Bus 未返回唯一连接名。");
            return new(connection, owner, serviceName);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            connection.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex.Message);
            connection.Dispose();
            return null;
        }
    }

    /// <summary>Completes with the folder chosen by Dolphin; the caller writes the files there.</summary>
    public async Task<string> WaitForDestinationAsync(CancellationToken ct)
    {
        try { return await _destination.Task.WaitAsync(DestinationTimeout, ct).ConfigureAwait(false); }
        catch (TimeoutException ex) { throw new IOException("目标未接收文件，请拖到 Dolphin 文件夹。", ex); }
    }

    public ValueTask HandleMethodAsync(MethodContext context)
    {
        if (context.IsDBusIntrospectRequest)
        {
            try { context.ReplyIntrospectXml([InterfaceXml]); }
            catch (Exception ex) { Debug.WriteLine(ex.Message); }
            return default;
        }

        if (!string.Equals(context.Request.InterfaceAsString, KdeArkDnd.ExtractInterface, StringComparison.Ordinal) ||
            !string.Equals(context.Request.MemberAsString, KdeArkDnd.ExtractMethod, StringComparison.Ordinal) ||
            !string.Equals(context.Request.SignatureAsString, "s", StringComparison.Ordinal))
        {
            try { context.ReplyUnknownMethodError(); }
            catch (Exception ex) { Debug.WriteLine(ex.Message); }
            return default;
        }

        string destination;
        try
        {
            destination = context.Request.GetBodyReader().ReadString();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex.Message);
            TryReplyError(context, "org.freedesktop.DBus.Error.InvalidArgs", "目标目录无效。");
            return default;
        }

        // The callback may arrive while the native drag loop is still unwinding. Its state is only
        // touched after the owner context runs, and the immediate D-Bus reply prevents the caller
        // from waiting for the entire phone download.
        context.DisposesAsynchronously = true;
        try
        {
            _owner.Post(static state =>
            {
                var (service, method, path) = ((KdeArkDropService, MethodContext, string))state!;
                service.ReceiveDestination(method, path);
            }, (this, context, destination));
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex.Message);
            TryDisposeContext(context);
        }
        return default;
    }

    private void ReceiveDestination(MethodContext context, string destination)
    {
        try
        {
            if (_disposed)
            {
                TryReplyError(context, "org.freedesktop.DBus.Error.Failed", "拖动已结束。");
                return;
            }

            if (_received)
            {
                TryReplyError(context, "org.freedesktop.DBus.Error.Failed", "拖动已接收。");
                return;
            }

            if (!Path.IsPathFullyQualified(destination) || !Directory.Exists(destination))
            {
                TryReplyError(context, "org.freedesktop.DBus.Error.InvalidArgs", "请选择本机文件夹。");
                _destination.TrySetException(new IOException("请选择现存的本机文件夹。"));
                return;
            }

            _received = true;
            if (!TryReply(context))
            {
                _received = false;
                _destination.TrySetException(new IOException("无法确认 Dolphin 已接收拖动。"));
                return;
            }

            _destination.TrySetResult(destination);
        }
        finally { TryDisposeContext(context); }
    }

    private static void TryDisposeContext(MethodContext context)
    {
        try { context.Dispose(); }
        catch (Exception ex) { Debug.WriteLine(ex.Message); }
    }

    private static bool TryReply(MethodContext context)
    {
        try
        {
            using var reply = context.CreateReplyWriter(null);
            context.Reply(reply.CreateMessage());
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex.Message);
            return false;
        }
    }

    private static void TryReplyError(MethodContext context, string name, string message)
    {
        try { context.ReplyError(name, message); }
        catch (Exception ex) { Debug.WriteLine(ex.Message); }
    }

    /// <summary>Must be called by the same context that receives callbacks.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _destination.TrySetCanceled();
        _connection.Dispose();
    }
}
