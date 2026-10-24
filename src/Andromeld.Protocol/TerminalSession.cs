using System.Threading.Channels;

namespace Andromeld.Protocol;

/// <summary>
///     One remote PTY. The socket reader produces output without waiting for a UI/consumer.
///     Exactly one consumer reads and acknowledges bytes; input and resize each require one serial caller.
///     Dispose and connection termination may race those operations. No terminal emulator lives here.
/// </summary>
public sealed class TerminalSession : IAsyncDisposable
{
    private readonly AndromeldClient _client;
    private readonly TaskCompletionSource<TerminalExit> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // APK chunks are at most 8192 bytes and stops after 128 KiB outstanding. 256 KiB
    // leaves room for the final chunk; overflow ends only this PTY, never stalls control traffic.
    private readonly Channel<ReadOnlyMemory<byte>> _output = Channel.CreateUnbounded<ReadOnlyMemory<byte>>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false });
    private int _buffered;
    private int _closed, _id;
    private long _delivered, _consumed, _acknowledged;

    internal TerminalSession(AndromeldClient client)
    {
        _client = client;
    }

    internal TaskCompletionSource Opened { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Id => Volatile.Read(ref _id);
    public bool IsClosed => Volatile.Read(ref _closed) != 0;
    public Task<TerminalExit> Completion => _completion.Task;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        Opened.TrySetCanceled();
        _output.Writer.TryComplete();
        _completion.TrySetResult(new TerminalExit(null));
        await _client.CloseTerminalAsync(this).ConfigureAwait(false);
    }

    internal static void ValidateSize(int columns, int rows)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(columns, 500);
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rows, 300);
    }

    internal void Bind(int id)
    {
        Volatile.Write(ref _id, id);
    }

    internal bool Enqueue(ReadOnlyMemory<byte> bytes)
    {
        if (IsClosed)
        {
            return true;
        }

        if (bytes.Length > 8192)
        {
            return false;
        }

        if (Interlocked.Add(ref _buffered, bytes.Length) <= 262144 && _output.Writer.TryWrite(bytes))
        {
            return true;
        }

        Interlocked.Add(ref _buffered, -bytes.Length);
        return false;
    }

    internal void FailOpen(Exception error)
    {
        Opened.TrySetException(error);
        End(new TerminalExit(null, error));
    }

    internal void End(TerminalExit exit)
    {
        Interlocked.Exchange(ref _closed, 1);
        _completion.TrySetResult(exit);
        _output.Writer.TryComplete();
    }

    public async ValueTask<ReadOnlyMemory<byte>> ReadOutputAsync(CancellationToken ct)
    {
        while (await _output.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            if (_output.Reader.TryRead(out ReadOnlyMemory<byte> chunk))
            {
                _delivered += chunk.Length;
                Interlocked.Add(ref _buffered, -chunk.Length);
                return chunk;
            }
        }

        return ReadOnlyMemory<byte>.Empty;
    }

    /// <summary>Call after the emulator has parsed these raw UTF-8 bytes, never on socket receipt.</summary>
    public async ValueTask AcknowledgeOutputAsync(int bytes, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bytes, _delivered - _consumed);

        _consumed += bytes;
        if (!IsClosed && _consumed - _acknowledged >= 32768)
        {
            await _client.AckTerminalAsync(Id, _consumed, ct).ConfigureAwait(false);
            _acknowledged = _consumed;
        }
    }

    public async Task SendInputAsync(ReadOnlyMemory<byte> input, CancellationToken ct)
    {
        // Bounded frames also keep a large paste from monopolising the shared control writer.
        while (!input.IsEmpty)
        {
            ObjectDisposedException.ThrowIf(IsClosed, this);
            int count = Math.Min(input.Length, 8192);
            await _client.SendTerminalInputAsync(Id, input[..count], ct).ConfigureAwait(false);
            input = input[count..];
        }
    }

    public Task ResizeAsync(int columns, int rows, CancellationToken ct)
    {
        ValidateSize(columns, rows);
        return IsClosed ? Task.CompletedTask : _client.ResizeTerminalAsync(Id, columns, rows, ct);
    }
}