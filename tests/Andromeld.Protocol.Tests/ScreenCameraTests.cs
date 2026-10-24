using Andromeld.Protocol;
using Andromeld.Testing;

namespace Andromeld.Protocol.Tests;

public static partial class Program
{
    private static async Task CheckScreenCameraAsync(AndromeldClient client, MockPhone server, PhoneApp app)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var mirror = await client.OpenMirrorAsync(app, stop.Token);
        await mirror.AttachAsync(stop.Token);
        byte[] jpeg = [0xff, 0xd8, 1, 2, 3, 0xff, 0xd9]; // Envelope-only synthetic vector, not a decodable image.
        Check(!await mirror.SendCameraFrameAsync(jpeg, stop.Token), "camera sends no frame before Android requests a stream");
        var notifications = new System.Collections.Concurrent.ConcurrentQueue<bool>();
        mirror.CameraRequestedChanged += notifications.Enqueue;
        await server.PushCameraStateAsync(mirror.SessionId, true);
        await UntilAsync(() => mirror.CameraRequested, stop.Token);
        Check(await mirror.SendCameraFrameAsync(jpeg, stop.Token), "fusion-camera on enables JPEG on the attached application media transport");
        await UntilAsync(() => server.CameraFrames.Any(f => f.SessionId == mirror.SessionId), stop.Token);
        Check(server.CameraFrames.Last().Jpeg.SequenceEqual(jpeg), "camera envelope is exactly 07 01 plus raw JPEG, without JSON or RTC fragmentation");
        foreach (var bad in new[] { Array.Empty<byte>(), new byte[] { 1, 2 }, new byte[1048577] })
        {
            try { await mirror.SendCameraFrameAsync(bad, stop.Token); throw new Exception("Invalid JPEG accepted"); }
            catch (ArgumentException) { Check(true, "invalid camera JPEG rejected before it can close the media socket"); }
        }
        await server.PushCameraStateAsync(mirror.SessionId, false);
        await UntilAsync(() => !mirror.CameraRequested, stop.Token);
        Check(!await mirror.SendCameraFrameAsync(jpeg, stop.Token) && notifications.SequenceEqual([true, false]), "fusion-camera off stops outgoing frames and notifies the presentation owner");
        Check((await client.GetRootsAsync(stop.Token)).Length > 0, "camera framing validation keeps the authenticated control connection usable");
    }
}
