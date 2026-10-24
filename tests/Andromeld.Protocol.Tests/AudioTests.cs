using System.Text;
using Andromeld.Protocol;
using Andromeld.Testing;

namespace Andromeld.Protocol.Tests;

public static partial class Program
{
    private static async Task CheckAudioAsync(AndromeldClient client, MockPhone server, PhoneApp app)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var mono = AudioMessageCodec.ReadConfiguration(Encoding.UTF8.GetBytes("{\"sampleRate\":48000,\"channels\":1,\"format\":\"pcm_s16le\"}"));
        Check(mono.BytesPerFrame == 2, "audio config matches Web/APK S16LE mono format");
        foreach (var bad in new[] { new AudioConfiguration(0, 1, "pcm_s16le"), new(48000, 0, "pcm_s16le"), new(48000, 3, "pcm_s16le"), new(48000, 2, "opus") })
        {
            try { bad.Validate(); throw new Exception("Unsupported audio accepted"); }
            catch (IOException) { }
        }
        Check(true, "unsupported audio rate, channel count and codec are rejected");
        foreach (var size in new[] { 7, 9, 1048586 })
        {
            try { AudioMessageCodec.ReadPacket(mono, new byte[size]); throw new Exception("Invalid audio length accepted"); }
            catch (IOException) { }
        }
        Check(true, "truncated, unaligned and oversized audio packets are rejected");
        await using var mirror = await client.OpenMirrorAsync(app, stop.Token);
        var received = System.Threading.Channels.Channel.CreateUnbounded<AudioPacket>();
        void Receive(AudioPacket packet) => received.Writer.TryWrite(packet);
        client.AudioReceived += Receive;
        try
        {
            await client.SetAudioEnabledAsync(true, mirror.SessionId, stop.Token);
            var packet = await received.Reader.ReadAsync(stop.Token);
            Check(packet.TimestampUs == MockPhone.AudioTimestamp && packet.Configuration == mono && packet.Data.Span.SequenceEqual(MockPhone.SyntheticPcm),
                "control [2,3] subscribes once; [2,1]/[2,2] preserve 64-bit big-endian timestamps and S16LE samples");
            await server.PushAudioAsync([0, 128, 255, 127], channels: 2, sampleRate: 44100);
            packet = await received.Reader.ReadAsync(stop.Token);
            Check(packet.Configuration.Channels == 2 && packet.Configuration.SampleRate == 44100 && packet.Data.Length == 4,
                "audio configuration changes travel with the corresponding PCM packet");
            var invalid = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Failed(Exception ex) => invalid.TrySetResult(ex);
            client.AudioFailed += Failed;
            try
            {
                await server.PushAudioAsync([1]);
                await invalid.Task.WaitAsync(stop.Token);
                Check((await client.GetAppsAsync(stop.Token)).Length == 18,
                    "malformed audio reports a local stream error while the control connection stays usable");
            }
            finally { client.AudioFailed -= Failed; }
            await client.SetAudioEnabledAsync(false, null, stop.Token);
            await UntilAsync(() => server.AudioSubscriptions.Any(m => !m.GetProperty("on").GetBoolean()), stop.Token);
            Check(server.AudioSubscriptions.Last().GetProperty("sessionId").GetString() == "", "audio off clears its session owner on control channel");
        }
        finally { client.AudioReceived -= Receive; }
    }
}
