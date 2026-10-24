using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;

namespace Andromeld.Testing;

public sealed partial class MockPhone
{
    public bool SupportsAudio { get; set; } = true;
    public ConcurrentQueue<JsonElement> AudioSubscriptions { get; } = new();
    private WebSocket? _audioSocket;
    public static byte[] SyntheticPcm => Enumerable.Range(0, 4800).SelectMany(i =>
        BitConverter.GetBytes((short)(Math.Sin(i * 2 * Math.PI * 440 / 48000) * 3000))).ToArray();
    public const ulong AudioTimestamp = 9_007_199_254_740_995UL;

    public async Task PushAudioAsync(byte[]? pcm = null, int channels = 1, int sampleRate = 48000)
    {
        var socket = _audioSocket ?? throw new InvalidOperationException("Audio is disabled");
        await SendAsync(socket, new { sampleRate, channels, format = "pcm_s16le" }, channel: 2, type: 1);
        var samples = pcm ?? SyntheticPcm;
        var payload = new byte[10 + samples.Length];
        payload[0] = 2; payload[1] = 2;
        BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(2), AudioTimestamp);
        samples.CopyTo(payload, 10);
        await SendBytesAsync(socket, payload, false);
    }

    private async Task HandleAudioSubscriptionAsync(WebSocket socket, byte[] bytes)
    {
        if (bytes[1] != 3) throw new IOException("Invalid audio subscription envelope");
        using var document = JsonDocument.Parse(bytes.AsMemory(2));
        var request = document.RootElement;
        AudioSubscriptions.Enqueue(request.Clone());
        if (!request.GetProperty("on").GetBoolean()) { _audioSocket = null; return; }
        if (!SupportsAudio || !_mirrors.ContainsKey(request.GetProperty("sessionId").GetString()!)) throw new IOException("Invalid audio owner");
        _audioSocket = socket;
        await PushAudioAsync();
    }
}
