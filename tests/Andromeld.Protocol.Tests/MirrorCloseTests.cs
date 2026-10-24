using System.Collections.Concurrent;
using System.Diagnostics;
using Andromeld.Protocol;
using Andromeld.Testing;

namespace Andromeld.Protocol.Tests;

public static partial class Program
{
    private static async Task CheckMirrorCloseAsync(AndromeldClient client, MockPhone server, PhoneApp app)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        byte[] jpeg = [0xff, 0xd8, 1, 2, 3, 0xff, 0xd9];
        await using var mirror = await client.OpenMirrorAsync(app, stop.Token);
        await mirror.AttachAsync(stop.Token);
        await server.PushCameraStateAsync(mirror.SessionId, true);
        await UntilAsync(() => mirror.CameraRequested, stop.Token);
        await mirror.SendCameraFrameAsync(jpeg, stop.Token);
        await UntilAsync(() => server.CameraFrames.Any(f => f.SessionId == mirror.SessionId), stop.Token);
        var taskStates = new ConcurrentQueue<bool>();
        mirror.TaskStateChanged += taskStates.Enqueue;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.MirrorCloseGate = release.Task;
        var closing = mirror.DisposeAsync().AsTask();
        try
        {
            await UntilAsync(() => server.ClosedMirrors.Contains(mirror.SessionId), stop.Token);
            Check(!closing.IsCompleted && !mirror.CameraRequested && !await mirror.SendCameraFrameAsync(jpeg, stop.Token),
                "closing an active camera sends close-window and stops JPEGs while waiting for the device acknowledgement");
            await server.PushTaskStateAsync(mirror.SessionId, true);
            await UntilAsync(() => taskStates.Contains(true), stop.Token);
            Check(!closing.IsCompleted, "media remains attached and receiving while the device closes its application session");
            await server.PushCameraStateAsync(mirror.SessionId, true);
            await server.PushTaskStateAsync(mirror.SessionId, false);
            await UntilAsync(() => taskStates.Contains(false), stop.Token);
            Check(!mirror.CameraRequested && !await mirror.SendCameraFrameAsync(jpeg, stop.Token),
                "a late camera-on during close cannot restart sharing");
        }
        finally { release.TrySetResult(); server.MirrorCloseGate = null; }
        await closing.WaitAsync(stop.Token);
        Check(!client.GetSessions().Any(s => s.SessionId == mirror.SessionId), "mirror disposal finishes after its own closed acknowledgement");

        await using var reopened = await client.OpenMirrorAsync(app, stop.Token);
        await reopened.AttachAsync(stop.Token);
        Check(reopened.SessionId != mirror.SessionId && !reopened.CameraRequested && !await reopened.SendCameraFrameAsync(jpeg, stop.Token),
            "reopening after camera close uses a fresh session without inheriting camera demand");
        await server.PushCameraStateAsync(reopened.SessionId, true);
        await UntilAsync(() => reopened.CameraRequested, stop.Token);
        await reopened.SendCameraFrameAsync(jpeg, stop.Token);
        await UntilAsync(() => server.CameraFrames.Any(f => f.SessionId == reopened.SessionId), stop.Token);
        Check(server.CameraFrames.Count(f => f.SessionId == mirror.SessionId) == 1,
            "the reopened camera supplies only the new media connection");
        await client.CloseMirrorAsync(reopened.SessionId, stop.Token);
        await UntilAsync(() => !client.GetSessions().Any(s => s.SessionId == reopened.SessionId), stop.Token);
        await reopened.DisposeAsync();
        Check(server.ClosedMirrors.Count(s => s == reopened.SessionId) == 1,
            "a device-closed session releases media without sending a duplicate close-window");

        await using var unresponsive = await client.OpenMirrorAsync(app, stop.Token);
        await unresponsive.AttachAsync(stop.Token);
        release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        server.MirrorCloseGate = release.Task;
        var elapsed = Stopwatch.StartNew();
        try
        {
            await unresponsive.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), stop.Token);
            Check(elapsed.Elapsed >= TimeSpan.FromSeconds(2.5) && elapsed.Elapsed < TimeSpan.FromSeconds(5),
                "a missing device close acknowledgement still releases local media after the bounded wait");
        }
        finally { release.TrySetResult(); server.MirrorCloseGate = null; }
    }
}
