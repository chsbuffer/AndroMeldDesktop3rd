using System.Buffers.Binary;
using System.Globalization;
using System.Net.WebSockets;
using System.Text.Json;

namespace Andromeld.Protocol;

/// <summary>One independently attached media WebSocket. Video ACKs belong on the control connection.</summary>
public sealed partial class MirrorSession : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly byte[] KeyFrameRequest = [1, 3];
    private readonly TaskCompletionSource _attached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly AndromeldClient _control;
    private readonly Uri _endpoint;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly ClientWebSocket _socket = new();
    private readonly string _token;
    private bool _disposed, _waitingForKey = true;
    private DragOutTransfer? _dragOut;
    private int? _lastDecorationTop;
    private double? _lastDensity;
    private Task? _reader;

    internal MirrorSession(AndromeldClient control, Uri endpoint, string token, MirrorState state)
    {
        _control = control;
        _endpoint = endpoint;
        _token = token;
        State = state;
    }

    public MirrorState State { get; }
    public string SessionId => State.SessionId;
    public Func<VideoConfiguration, ValueTask>? ConfigureVideo { get; set; }
    public Func<VideoPacket, ValueTask>? ReceiveVideo { get; set; }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SetCameraRequested(false);
        // Match Web: request the device-side close while media is still attached. Aborting
        // first enters the server's disconnect/grace path while it is releasing cameras.
        using CancellationTokenSource stop = new(TimeSpan.FromSeconds(3));
        try { await _control.CloseMirrorAndWaitAsync(SessionId, stop.Token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            /* Control connection may already be gone. */
        }
        finally
        {
            // A missing close acknowledgement must not keep local resources alive forever.
            await _lifetime.CancelAsync().ConfigureAwait(false);
            _socket.Abort();
            if (_reader is not null)
            {
                await _reader.ConfigureAwait(false);
            }

            _socket.Dispose();
            _send.Dispose();
            _lifetime.Dispose();
        }
    }

    public event Action<DragOutTransfer>? DragOutStarted;
    public event Action<Exception>? Disconnected;
    /// <summary>Raised on the media reader; consumers marshal presentation changes to their UI thread.</summary>
    public event Action<bool>? TaskStateChanged;

    public async Task AttachAsync(CancellationToken ct)
    {
        _socket.Options.Proxy = null;
        _socket.Options.SetRequestHeader("Origin", AndromeldClient.Origin);
        _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        using CancellationTokenSource dial = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        dial.CancelAfter(TimeSpan.FromSeconds(8));
        await _socket.ConnectAsync(_endpoint, dial.Token).ConfigureAwait(false);
        await SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { t = "attach", proto = 2, token = _token, sessionId = SessionId }), true, dial.Token)
            .ConfigureAwait(false);
        _reader = ReadAsync();
        await _attached.Task.WaitAsync(dial.Token).ConfigureAwait(false);
        await RequestKeyFrameAsync(ct).ConfigureAwait(false);
        if (!State.IsPhone)
        {
            await ConfigureDecorationAsync(0, ct).ConfigureAwait(false);
        }
    }

    public Task RequestKeyFrameAsync(CancellationToken ct)
    {
        return SendAsync(KeyFrameRequest, false, ct);
    }

    public Task RelaunchAsync(CancellationToken ct)
    {
        return SendControlAsync(new { c = "relaunch" }, ct);
    }

    public Task PointerAsync(string action, int x, int y, CancellationToken ct)
    {
        return SendControlAsync(new
        {
            c = "input",
            k = "pointer",
            a = action,
            x,
            y,
            p = 0
        }, ct);
    }

    public Task ScrollAsync(int x, int y, double dx, double dy, CancellationToken ct)
    {
        return SendControlAsync(new
        {
            c = "input",
            k = "scroll",
            x,
            y,
            dx,
            dy
        }, ct);
    }

    public Task TextAsync(string text, CancellationToken ct)
    {
        return SendControlAsync(new { c = "input", k = "text", text }, ct);
    }

    public async Task ResizeAsync(MirrorSize size, CancellationToken ct)
    {
        if (State.IsPhone)
        {
            return; // A physical screen does not accept virtual display configuration.
        }

        if (size.Width <= 0 || size.Height <= 0 || !double.IsFinite(size.Dpr) || size.Dpr <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size));
        }

        await SendControlAsync(new { c = "resize", w = size.Width, h = size.Height, dpr = size.Dpr }, ct).ConfigureAwait(false);
        await RequestKeyFrameAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The session owner awaits each display operation before starting another.</summary>
    public async Task ConfigureDisplayAsync(MirrorDisplayConfiguration configuration, CancellationToken ct)
    {
        if (State.IsPhone)
        {
            return;
        }

        if (!double.IsFinite(configuration.Scale) || configuration.Scale is < .2 or > 1.5 ||
            configuration.Size.Width <= 0 || configuration.Size.Height <= 0 ||
            !double.IsFinite(configuration.Size.Dpr) || configuration.Size.Dpr <= 0 || configuration.DecorationTop < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(configuration));
        }

        if (!_lastDensity.Equals(configuration.Scale))
        {
            await SendControlAsync(new { c = "density", scale = configuration.Scale }, ct).ConfigureAwait(false);
            _lastDensity = configuration.Scale;
        }

        // Web also sends resize when only zoom changes, then requests a keyframe.
        MirrorSize size = configuration.Size;
        await SendControlAsync(new { c = "resize", w = size.Width, h = size.Height, dpr = size.Dpr }, ct).ConfigureAwait(false);
        await ConfigureDecorationAsync(configuration.DecorationTop, ct).ConfigureAwait(false);
        await RequestKeyFrameAsync(ct).ConfigureAwait(false);
    }

    private async Task ConfigureDecorationAsync(int top, CancellationToken ct)
    {
        if (_lastDecorationTop == top)
        {
            return;
        }

        await SendControlAsync(new { c = "decor-insets", top }, ct).ConfigureAwait(false);
        _lastDecorationTop = top;
    }

    public async Task KeyAsync(int code, int meta, CancellationToken ct)
    {
        await SendControlAsync(new
        {
            c = "input",
            k = "key",
            a = "down",
            code,
            meta
        }, ct).ConfigureAwait(false);
        await SendControlAsync(new
        {
            c = "input",
            k = "key",
            a = "up",
            code,
            meta
        }, ct).ConfigureAwait(false);
    }

    internal Task SendControlAsync(object value, CancellationToken ct)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(value);
        byte[] bytes = new byte[json.Length + 2];
        bytes[0] = 4;
        bytes[1] = 1;
        json.CopyTo(bytes, 2);
        return SendAsync(bytes, false, ct);
    }

    private async Task SendAsync(ReadOnlyMemory<byte> bytes, bool text, CancellationToken ct)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        await _send.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            await _socket.SendAsync(bytes, text ? WebSocketMessageType.Text : WebSocketMessageType.Binary, true, linked.Token).ConfigureAwait(false);
        }
        finally { _send.Release(); }
    }

    private async Task ReadAsync()
    {
        Exception failure = new IOException("镜像连接已断开，请关闭窗口后重新打开。");
        try
        {
            byte[] buffer = new byte[65536];
            while (!_lifetime.IsCancellationRequested)
            {
                using MemoryStream message = new();
                ValueWebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(buffer.AsMemory(), _lifetime.Token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        throw failure;
                    }

                    if (message.Length + result.Count > 8 * 1024 * 1024)
                    {
                        throw new IOException("镜像数据包超过 8 MiB。");
                    }

                    await message.WriteAsync(buffer.AsMemory(0, result.Count), _lifetime.Token).ConfigureAwait(false);
                } while (!result.EndOfMessage);

                byte[] bytes = message.ToArray();
                if (result.MessageType == WebSocketMessageType.Text)
                {
                    using JsonDocument doc = JsonDocument.Parse(bytes);
                    JsonElement value = doc.RootElement;
                    if (string.Equals(value.GetProperty("t").GetString(), "attached", StringComparison.Ordinal) &&
                        string.Equals(value.GetProperty("sessionId").GetString(), SessionId, StringComparison.Ordinal))
                    {
                        _attached.TrySetResult();
                    }
                    else
                    {
                        throw new IOException("手机拒绝媒体连接：" + (value.TryGetProperty("detail", out JsonElement detail) ? detail.GetString() : "会话不匹配"));
                    }
                }
                else if (bytes.Length >= 2 && bytes[0] == 4 && bytes[1] == 1)
                {
                    using JsonDocument doc = JsonDocument.Parse(bytes.AsMemory(2));
                    JsonElement value = doc.RootElement;
                    if (HandlePhoneControl(value))
                    {
                        continue;
                    }

                    if (value.TryGetProperty("c", out JsonElement command) &&
                        string.Equals(command.GetString(), "task-state", StringComparison.Ordinal) &&
                        value.TryGetProperty("away", out JsonElement away))
                    {
                        TaskStateChanged?.Invoke(away.GetBoolean());
                    }

                    if (string.Equals(command.GetString(), "fusion-camera", StringComparison.Ordinal))
                    {
                        SetCameraRequested(value.GetProperty("on").GetBoolean());
                    }

                    if (string.Equals(command.GetString(), "drag-out", StringComparison.Ordinal) &&
                        value.TryGetProperty("txn", out JsonElement transaction))
                    {
                        string? txn = transaction.GetString();
                        if (txn is null || !long.TryParse(txn, CultureInfo.InvariantCulture, out _))
                        {
                            throw new IOException("拖出事务编号无效。");
                        }

                        if (string.Equals(value.GetProperty("t").GetString(), "live", StringComparison.Ordinal))
                        {
                            _dragOut?.Fail(new IOException("另一次拖动已开始。"));
                            _dragOut = new DragOutTransfer(this, txn, _lifetime.Token);
                            DragOutStarted?.Invoke(_dragOut);
                        }
                        else if (_dragOut is { } dragOut && string.Equals(dragOut.TransactionId, txn, StringComparison.Ordinal))
                        {
                            dragOut.Control(value);
                        }
                    }
                }
                else if (bytes.Length >= 2 && bytes[0] == 5 && bytes[1] == 5)
                {
                    _dragOut?.Chunk(bytes.AsMemory(2));
                }
                else if (bytes.Length >= 2 && bytes[0] == 1)
                {
                    if (bytes[1] == 1)
                    {
                        using JsonDocument doc = JsonDocument.Parse(bytes.AsMemory(2));
                        VideoConfiguration config = doc.RootElement.Deserialize<VideoConfiguration>(Json)!;
                        if (config.W <= 0 || config.H <= 0 || config.W > 8192 || config.H > 8192 ||
                            !config.Codec.StartsWith("avc1", StringComparison.OrdinalIgnoreCase))
                        {
                            throw new IOException("手机返回了不支持的视频配置。");
                        }

                        _waitingForKey = true;
                        if (ConfigureVideo is { } configure)
                        {
                            await configure(config).ConfigureAwait(false);
                        }
                    }
                    else if (bytes[1] == 2)
                    {
                        if (bytes.Length < 20)
                        {
                            throw new IOException("镜像视频帧不完整。");
                        }

                        ulong acknowledged = BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(2));
                        ulong timestamp = BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(10));
                        bool key = (bytes[18] & 1) != 0;
                        if (!_waitingForKey || key)
                        {
                            _waitingForKey = false;
                            if (ReceiveVideo is { } receive)
                            {
                                await receive(new VideoPacket(acknowledged, timestamp, key, bytes.AsMemory(19))).ConfigureAwait(false);
                            }
                        }

                        await _control.AcknowledgeVideoAsync(SessionId, acknowledged, _lifetime.Token).ConfigureAwait(false);
                    }
                    else if (bytes[1] == 3)
                    {
                        _waitingForKey = true;
                        await RequestKeyFrameAsync(_lifetime.Token).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            _dragOut?.Fail(failure);
            SetCameraRequested(false);
            _attached.TrySetException(failure);
            if (!_disposed)
            {
                Disconnected?.Invoke(failure);
            }
        }
    }
}
