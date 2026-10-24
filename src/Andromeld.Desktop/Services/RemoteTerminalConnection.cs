using Andromeld.Protocol;

using Avalonia.Threading;

using Porta.Pty;

namespace Andromeld.Desktop.Services;

/// <summary>
///     Adapter only: never starts a local process. Resize/dispose belong to UI; the control owns
///     one output reader and serialises input writes. The protocol socket never waits for this reader.
/// </summary>
internal sealed class RemoteTerminalConnection : IPtyConnection
{
    private readonly Action<Exception> _report;
    private readonly TerminalSession _session;
    private readonly CancellationTokenSource _stop = new();
    private Task? _resize;
    private (int Columns, int Rows) _size;

    public RemoteTerminalConnection(TerminalSession session, Action<Exception> report)
    {
        _session = session;
        _report = report;
        ReaderStream = new OutputStream(session);
        WriterStream = new InputStream(session, _stop.Token);
    }

    // EOF is reported after all queued output has been parsed, rather than cutting off the tail
    // when term-exit arrives. TerminalControl uses WaitForExit/ExitCode on that EOF path.
    public event EventHandler<PtyExitedEventArgs>? ProcessExited
    {
        add
        {
            /* EOF is the ordered exit notification; no premature process event. */
        }
        remove
        {
            /* No handlers are retained. */
        }
    }
    public bool SupportsCancellableRead => true;
    public Stream ReaderStream { get; }
    public Stream WriterStream { get; }
    public int Pid => 0;
    public int ExitCode => _session.Completion.IsCompletedSuccessfully ? _session.Completion.Result.Code ?? -1 : -1;

    public bool WaitForExit(int milliseconds)
    {
        return _session.Completion.IsCompleted;
    }

    public void Kill()
    {
        _ = _session.DisposeAsync().AsTask();
    }

    public void Resize(int cols, int rows)
    {
        Dispatcher.UIThread.VerifyAccess();
        _size = (Math.Clamp(cols, 1, 500), Math.Clamp(rows, 1, 300));
        if (_resize is null || _resize.IsCompleted)
        {
            _resize = ResizeLoopAsync();
        }
    }

    public void Dispose()
    {
        Dispatcher.UIThread.VerifyAccess();
        _stop.Cancel();
        // The model owns and closes the remote session. Detached readers are cancelled by the
        // control before this adapter is disposed; no synchronously blocking UI/thread join here.
        _stop.Dispose();
    }

    private async Task ResizeLoopAsync()
    {
        try
        {
            (int Columns, int Rows) sent;
            do
            {
                sent = _size;
                await _session.ResizeAsync(sent.Columns, sent.Rows, _stop.Token).ConfigureAwait(true);
            } while (!_stop.IsCancellationRequested && sent != _size);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            /* Window/session is closing. */
        }
        catch (Exception ex)
        {
            if (!_stop.IsCancellationRequested)
            {
                _report(ex);
            }
        }
    }

    private abstract class TerminalStream : Stream
    {
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class OutputStream(TerminalSession session) : TerminalStream
    {
        private int _previousRead;
        private ReadOnlyMemory<byte> _remaining;
        public override bool CanRead => true;
        public override bool CanWrite => false;

        public override int Read(byte[] buffer, int offset, int count)
        {
            return ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.IsEmpty)
            {
                return 0;
            }

            // Iciclecreek 4.1.0 ReadPtyOutputAsync calls ConsumeOutputChunk (Terminal.Write(bytes))
            // synchronously before its next read. That next read is our parser-consumption barrier.
            // OutputReceived fires BEFORE parsing and must not be used as an ACK boundary.
            await session.AcknowledgeOutputAsync(_previousRead, cancellationToken).ConfigureAwait(false);
            _previousRead = 0;
            if (_remaining.IsEmpty)
            {
                _remaining = await session.ReadOutputAsync(cancellationToken).ConfigureAwait(false);
            }

            int count = Math.Min(buffer.Length, _remaining.Length);
            _remaining[..count].CopyTo(buffer);
            _remaining = _remaining[count..];
            _previousRead = count;
            return count;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class InputStream(TerminalSession session, CancellationToken stop) : TerminalStream
    {
        public override bool CanRead => false;
        public override bool CanWrite => true;

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(stop, cancellationToken);
            await session.SendInputAsync(buffer, linked.Token).ConfigureAwait(false);
        }
    }
}