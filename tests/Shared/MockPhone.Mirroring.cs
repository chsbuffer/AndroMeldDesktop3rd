using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;

namespace Andromeld.Testing;

public sealed partial class MockPhone
{
    private readonly ConcurrentDictionary<string, bool> _mirrors = new();
    private readonly ConcurrentDictionary<string, WebSocket> _mirrorControls = new();
    private int _nextMirror;
    public ConcurrentQueue<string> ClosedMirrors { get; } = new();
    public ConcurrentQueue<JsonElement> OpenedMirrors { get; } = new();
    public ConcurrentQueue<(string SessionId, ulong Bytes)> VideoAcks { get; } = new();
    public ConcurrentQueue<(string SessionId, JsonElement Message)> MediaInputs { get; } = new();
    public int KeyFrameRequests;
    public int OpenDelayMs;
    public Task? MirrorCloseGate { get; set; }
    public bool RecoverTaskAfterBack;
    public bool CloseWindowOnBack;
    public string? IntentOpenFailure;
    public Andromeld.Protocol.FocusedApplication? InitialFocusedApplication;
    public bool InitiallyScreenOff;
    public Task PushFocusedApplicationAsync(string sessionId, string? package, int? userId = 0) =>
        SendAsync(_mediaSockets[sessionId], new { c = "focused-app", pkg = package, userId });
    public Task PushDisplayPowerAsync(string sessionId, bool on) => SendAsync(_mediaSockets[sessionId], new { c = "display-power", on });
    public Task PushDisplayPowerStateAsync(string sessionId, int state, int reason) =>
        SendAsync(_mediaSockets[sessionId], new { c = "display-power-state", displayId = 0, state, reason });
    public ConcurrentQueue<(string SessionId, byte[] Jpeg)> CameraFrames { get; } = new();
    public Task PushCameraStateAsync(string sessionId, bool on) => SendAsync(_mediaSockets[sessionId], new { c = "fusion-camera", on });
    public Task PushTaskStateAsync(string sessionId, bool away) => SendAsync(_mediaSockets[sessionId], new { c = "task-state", away });
    public const ulong VideoCounter = 9_007_199_254_740_993UL;

    private async Task OpenMirrorAsync(WebSocket socket, JsonElement request)
    {
        var phone = request.TryGetProperty("kind", out var kind) && kind.GetString() == "phone";
        var intent = request.TryGetProperty("intentUrl", out var intentUrl);
        var package = phone || intent ? "" : request.GetProperty("pkg").GetString();
        if (intent && (!Andromeld.Protocol.AndroidIntentUri.TryParse(intentUrl.GetString(), out _) || request.TryGetProperty("pkg", out _) || request.TryGetProperty("userId", out _) || request.TryGetProperty("kind", out _)))
            throw new Exception("Invalid intent target fields");
        if (!phone && (request.GetProperty("w").GetInt32() <= 0 || request.GetProperty("h").GetInt32() <= 0 || request.GetProperty("dpr").GetDouble() <= 0))
            throw new Exception("Invalid application display dimensions");
        OpenedMirrors.Enqueue(request.Clone());
        if (OpenDelayMs > 0) await Task.Delay(OpenDelayMs);
        if (package == "test.fail" || (intent && IntentOpenFailure is not null))
        {
            await SendAsync(socket, new { c = "window-state", sessionId = "", state = "failed", pkg = package, reason = IntentOpenFailure ?? "pool-full" });
            return;
        }
        var sessionId = "mock-" + Interlocked.Increment(ref _nextMirror);
        _mirrors[sessionId] = phone;
        _mirrorControls[sessionId] = socket;
        await SendAsync(socket, new { c = "window-state", sessionId, state = "opening", pkg = package, kind = phone ? "phone" : null });
        await SendAsync(socket, new { c = "window-state", sessionId, state = "live", pkg = package, kind = phone ? "phone" : null });
    }

    private async Task ServeMediaAsync(WebSocket socket)
    {
        var (kind, bytes) = await ReadAsync(socket);
        using var auth = JsonDocument.Parse(bytes);
        var attach = auth.RootElement;
        if (kind != WebSocketMessageType.Text || attach.GetProperty("t").GetString() != "attach" || attach.GetProperty("proto").GetInt32() != 2 || attach.GetProperty("token").GetString() != "test-token")
            throw new Exception("Invalid media authentication");
        var sessionId = attach.GetProperty("sessionId").GetString()!;
        if (!_mirrors.TryGetValue(sessionId, out var phone)) throw new Exception("Unknown media session");
        await SendAsync(socket, new { t = "attached", sessionId }, true);
        _mediaSockets[sessionId] = socket;
        if (phone)
        {
            await PushFocusedApplicationAsync(sessionId, InitialFocusedApplication?.PackageName, InitialFocusedApplication?.UserId ?? 0);
            if (InitiallyScreenOff) await PushDisplayPowerAsync(sessionId, false);
        }
        await SendAsync(socket, new { c = "task-state", away = false });
        await SendAsync(socket, new { codec = "avc1.42C01E", w = phone ? 240 : 320, h = phone ? 320 : 240, fps = 60, dpr = 1 }, channel: 1, type: 1);
        var data = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, phone ? "Media/red-240x320.h264" : "Media/blue-320x240.h264"));
        await SendVideoAsync(socket, [0, 0, 1, 0x41], VideoCounter - 1, false);
        await SendVideoAsync(socket, data, VideoCounter, true);
        await SendBytesAsync(socket, [1, 3], false); // Simulate a dropped delta and require a fresh keyframe.
        await SendVideoAsync(socket, [0, 0, 1, 0x41], VideoCounter + 1, false);
        await SendVideoAsync(socket, data, VideoCounter + 2, true);
        while (socket.State == WebSocketState.Open)
        {
            var (_, packet) = await ReadAsync(socket);
            if (packet.Length == 2 && packet[0] == 1 && packet[1] == 3) Interlocked.Increment(ref KeyFrameRequests);
            else if (packet.Length >= 4 && packet[0] == 7 && packet[1] == 1)
            {
                if (packet.Length > 1048578 || packet[2] != 0xff || packet[3] != 0xd8) throw new IOException("Invalid JPEG envelope");
                CameraFrames.Enqueue((sessionId, packet[2..]));
            }
            else if (packet.Length >= 2 && packet[0] == 4 && packet[1] == 1)
            {
                using var command = JsonDocument.Parse(packet.AsMemory(2));
                if (command.RootElement.GetProperty("c").GetString() == "video-ack") throw new Exception("ACK sent on media instead of control WebSocket");
                MediaInputs.Enqueue((sessionId, command.RootElement.Clone()));
                var value = command.RootElement;
                if (value.GetProperty("c").GetString()?.StartsWith("drag-out-", StringComparison.Ordinal) == true)
                { await HandleDragOutAsync(socket, sessionId, value); continue; }
                if (value.GetProperty("c").GetString() == "relaunch") await SendAsync(socket, new { c = "task-state", away = false });
                if (value.GetProperty("c").GetString() == "input" && value.TryGetProperty("code", out var code) && code.GetInt32() == 4 &&
                    value.GetProperty("a").GetString() == "up" && !phone)
                {
                    if (CloseWindowOnBack) await SendAsync(_mirrorControls[sessionId], new { c = "window-state", sessionId, state = "closed", pkg = "test.app1" });
                    else
                    {
                        await SendAsync(socket, new { c = "task-state", away = true });
                        if (RecoverTaskAfterBack)
                        {
                            await Task.Delay(50);
                            await SendAsync(socket, new { c = "task-state", away = false });
                        }
                    }
                }
            }
            else throw new Exception("Invalid media envelope");
        }
    }

    private static Task SendVideoAsync(WebSocket socket, byte[] encoded, ulong counter, bool key)
    {
        var packet = new byte[encoded.Length + 19]; packet[0] = 1; packet[1] = 2;
        BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(2), counter);
        BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(10), 123456);
        packet[18] = key ? (byte)1 : (byte)0;
        encoded.CopyTo(packet, 19);
        return SendBytesAsync(socket, packet, false);
    }
}
