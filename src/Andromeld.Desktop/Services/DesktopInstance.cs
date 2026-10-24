using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Threading.Channels;

namespace Andromeld.Desktop.Services;

/// <summary>A named mutex elects the application instance; a named pipe carries launch requests.
/// The listener only queues requests. Avalonia's UI thread owns the single channel reader and all windows.</summary>
internal sealed class DesktopInstance : IDisposable
{
    private const int MaximumPathBytes = 131072;
    private static readonly byte[] AcceptedResponse = [1];
    private readonly Mutex? _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<string?> _requests = Channel.CreateUnbounded<string?>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private Task? _listener;
    private bool _disposed;

    public DesktopInstance(string identifier)
    {
        // Keep the named object alive without taking thread-bound mutex ownership.
        // Unix terminal sessions differ even when their processes belong to the same desktop user.
        var mutex = new Mutex(false, identifier, new NamedWaitHandleOptions { CurrentUserOnly = true, CurrentSessionOnly = false }, out bool created);
        IsFirstInstance = created;
        if (created) _mutex = mutex;
        else mutex.Dispose();
        _pipeName = identifier + ".files";
    }

    public bool IsFirstInstance { get; }
    public ChannelReader<string?> Requests => _requests.Reader;
    public Task ListenerCompletion => _listener ?? Task.CompletedTask;

    public static string? GetPackagePath(string[] args) => args.Length == 0 ? null : Path.GetFullPath(args[0]);

    public void StartListening(string? initialPath)
    {
        if (!IsFirstInstance || _listener is not null) throw new InvalidOperationException("Only the first instance can start listening once.");
        var server = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        _requests.Writer.TryWrite(initialPath);
        _listener = ListenAsync(server, _lifetime.Token);
    }

    public async Task ForwardAsync(string? path, CancellationToken ct = default)
    {
        if (IsFirstInstance) throw new InvalidOperationException("The first instance handles requests locally.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(timeout.Token).ConfigureAwait(false);
        byte[] bytes = path is null ? [] : Encoding.UTF8.GetBytes(path);
        if (bytes.Length > MaximumPathBytes) throw new ArgumentException("安装包路径过长。", nameof(path));
        byte[] header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await client.WriteAsync(header, timeout.Token).ConfigureAwait(false);
        await client.WriteAsync(bytes, timeout.Token).ConfigureAwait(false);
        await client.ReadExactlyAsync(header.AsMemory(0, 1), timeout.Token).ConfigureAwait(false);
        if (header[0] != 1) throw new IOException("已有实例未接受安装包请求。");
    }

    private async Task ListenAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        using (server)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                    try
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        timeout.CancelAfter(TimeSpan.FromSeconds(5));
                        byte[] header = new byte[sizeof(int)];
                        await server.ReadExactlyAsync(header, timeout.Token).ConfigureAwait(false);
                        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
                        if (length is < 0 or > MaximumPathBytes) continue;
                        byte[] bytes = new byte[length];
                        await server.ReadExactlyAsync(bytes, timeout.Token).ConfigureAwait(false);
                        string? path = length == 0 ? null : Encoding.UTF8.GetString(bytes);
                        await _requests.Writer.WriteAsync(path, ct).ConfigureAwait(false);
                        await server.WriteAsync(AcceptedResponse, timeout.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested && ex is IOException or OperationCanceledException)
                    {
                        // An incomplete request does not stop later launches from reaching the UI.
                    }
                    finally
                    {
                        server.Disconnect();
                    }
                }
            }
            catch (Exception ex) when (ct.IsCancellationRequested && ex is OperationCanceledException or IOException)
            {
                // Normal application shutdown.
            }
            catch (Exception ex)
            {
                _requests.Writer.TryComplete(ex);
                throw;
            }
            finally
            {
                _requests.Writer.TryComplete();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        // The listener never waits for UI work, so stopping it cannot depend on the calling thread.
        try { _listener?.GetAwaiter().GetResult(); }
        finally { _mutex?.Dispose(); _lifetime.Dispose(); }
    }
}
