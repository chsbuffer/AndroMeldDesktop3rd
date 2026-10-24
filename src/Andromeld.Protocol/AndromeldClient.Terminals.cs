using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;

namespace Andromeld.Protocol;

public sealed partial class AndromeldClient
{
    private readonly ConcurrentDictionary<string, TerminalSession> _terminalOpens = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<int, TerminalSession> _terminals = new();

    public async Task<TerminalSession> OpenTerminalAsync(int columns, int rows, CancellationToken ct)
    {
        TerminalSession.ValidateSize(columns, rows);
        if (!Capabilities.Contains("terminal"))
        {
            throw new NotSupportedException("设备未提供终端功能。");
        }

        ObjectDisposedException.ThrowIf(_disposed, this);
        string requestId = "terminal:" + Guid.NewGuid().ToString("N");
        TerminalSession session = new(this);
        _terminalOpens[requestId] = session;
        try
        {
            await SendJsonAsync(new { c = "term-open", reqId = requestId, cols = columns, rows }, false, 4, 1, ct).ConfigureAwait(false);
            await session.Opened.Task.WaitAsync(TimeSpan.FromSeconds(25), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return session;
        }
        catch
        {
            // Keep the registration until the reply/disconnect. A cancelled open can still allocate
            // a remote PTY, and that late id must be closed rather than abandoned.
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal Task SendTerminalInputAsync(int id, ReadOnlyMemory<byte> input, CancellationToken ct)
    {
        byte[] frame = new byte[input.Length + 6];
        frame[0] = 6;
        frame[1] = 1;
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(2), id);
        input.CopyTo(frame.AsMemory(6));
        return SendBytesAsync(frame, false, ct);
    }

    internal Task ResizeTerminalAsync(int id, int columns, int rows, CancellationToken ct)
    {
        return SendJsonAsync(new { c = "term-resize", id, cols = columns, rows }, false, 4, 1, ct);
    }

    internal Task AckTerminalAsync(int id, long received, CancellationToken ct)
    {
        return SendJsonAsync(new { c = "term-ack", id, received }, false, 4, 1, ct);
    }

    internal async Task CloseTerminalAsync(TerminalSession session)
    {
        if (session.Id <= 0)
        {
            return;
        }

        _terminals.TryRemove(new KeyValuePair<int, TerminalSession>(session.Id, session));
        if (_disposed || _socket.State != WebSocketState.Open)
        {
            return;
        }

        try
        {
            await SendJsonAsync(new { c = "term-close", id = session.Id }, false, 4, 1, _lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException)
        {
            /* The control connection owns every PTY and releases them on disconnect. */
        }
    }

    private bool HandleTerminalControl(JsonElement value)
    {
        if (!value.TryGetProperty("c", out JsonElement c))
        {
            return false;
        }

        switch (c.GetString())
        {
            case "term-opened":
                if (!value.TryGetProperty("reqId", out JsonElement request) || request.GetString() is not { } requestId ||
                    !_terminalOpens.TryRemove(requestId, out TerminalSession? session))
                {
                    return true;
                }

                if (value.TryGetProperty("err", out JsonElement err) && err.ValueKind == JsonValueKind.String)
                {
                    session.FailOpen(new IOException(string.Equals(err.GetString(), "limit", StringComparison.Ordinal) ? "手机最多同时打开 4 个终端。" : $"手机无法打开终端：{err.GetString()}"));
                    return true;
                }

                if (!value.TryGetProperty("id", out JsonElement id) || !id.TryGetInt32(out int number) || number <= 0)
                {
                    session.FailOpen(new IOException("终端响应缺少有效会话编号。"));
                    return true;
                }

                session.Bind(number);
                if (!_terminals.TryAdd(number, session))
                {
                    throw new IOException("手机重复使用了活动终端编号。");
                }

                if (session.IsClosed)
                {
                    _ = CloseTerminalAsync(session);
                }

                session.Opened.TrySetResult();
                return true;
            case "term-exit":
                if (value.TryGetProperty("id", out JsonElement exitId) && exitId.TryGetInt32(out int key) &&
                    _terminals.TryRemove(key, out TerminalSession? ended))
                {
                    ended.End(new TerminalExit(value.TryGetProperty("code", out JsonElement code) && code.TryGetInt32(out int result) ? result : -1));
                }

                return true;
            default: return false;
        }
    }

    private void HandleTerminalOutput(ReadOnlyMemory<byte> payload)
    {
        int id = BinaryPrimitives.ReadInt32BigEndian(payload.Span);
        if (_terminals.TryGetValue(id, out TerminalSession? session) && payload.Length > 4 && !session.Enqueue(payload[4..]))
        {
            session.End(new TerminalExit(null, new IOException("终端输出超过接收缓冲上限。")));
            _ = CloseTerminalAsync(session);
        }
    }

    private void FailTerminals(Exception failure)
    {
        foreach (KeyValuePair<string, TerminalSession> pair in _terminalOpens)
        {
            if (_terminalOpens.TryRemove(pair.Key, out TerminalSession? opening))
            {
                opening.FailOpen(failure);
            }
        }

        foreach (KeyValuePair<int, TerminalSession> pair in _terminals)
        {
            if (_terminals.TryRemove(pair.Key, out TerminalSession? session))
            {
                session.End(new TerminalExit(null, failure));
            }
        }
    }
}