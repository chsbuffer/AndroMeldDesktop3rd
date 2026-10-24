using System.Collections.Concurrent;
using Andromeld.Protocol;
using Andromeld.Testing;

namespace Andromeld.Protocol.Tests;

public static partial class Program
{
    private static async Task CheckPhoneMirrorControlsAsync()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var server = await MockPhone.StartAsync();
        server.InitialFocusedApplication = new("test.app0", 10);
        server.InitiallyScreenOff = true;
        await using var client = new AndromeldClient();
        await client.ConnectAsync(server.Endpoint, "synthetic-phone-controls", null, null, stop.Token);
        await using var phone = await client.OpenMirrorAsync(null, stop.Token);
        var focused = new ConcurrentQueue<FocusedApplication>();
        var powers = new ConcurrentQueue<bool>();
        var states = new ConcurrentQueue<DisplayPowerState>();
        phone.FocusedApplicationChanged += focused.Enqueue;
        phone.DisplayPowerChanged += powers.Enqueue;
        phone.DisplayPowerStateChanged += states.Enqueue;
        await phone.AttachAsync(stop.Token);
        await UntilAsync(() => focused.Count == 1 && powers.Count == 1, stop.Token);
        Check(focused.Single() == new FocusedApplication("test.app0", 10) && !powers.Single(),
            "phone attach replays typed focused application and held display power without a client request");
        await server.PushFocusedApplicationAsync(phone.SessionId, null, null);
        await server.PushDisplayPowerStateAsync(phone.SessionId, 1, 4);
        await UntilAsync(() => focused.Count == 2 && states.Count == 1, stop.Token);
        Check(focused.Last() == new FocusedApplication(null) && states.Single() == new DisplayPowerState(0, 1, 4),
            "empty foreground application normalizes user ID and display power state preserves protocol numbers");

        await phone.SetDisplayPowerAsync(false, stop.Token);
        await phone.SetDisplayPowerAsync(true, stop.Token);
        await using var application = await client.OpenMirrorAsync(new("test.app0", "合成应用", 0, false, -1, false), stop.Token);
        await application.AttachAsync(stop.Token);
        await application.SetDisplayPowerAsync(false, stop.Token);
        // A following input establishes that the server has consumed the application's media writes.
        await application.TextAsync("synthetic barrier", stop.Token);
        await UntilAsync(() => server.MediaInputs.Count(i => i.Message.GetProperty("c").GetString() == "display-power") == 2 &&
            server.MediaInputs.Any(i => i.SessionId == application.SessionId && i.Message.TryGetProperty("text", out _)), stop.Token);
        var requests = server.MediaInputs.Where(i => i.Message.GetProperty("c").GetString() == "display-power").ToArray();
        Check(requests.Length == 2 && requests.All(i => i.SessionId == phone.SessionId) &&
            requests.Select(i => i.Message.GetProperty("on").GetBoolean()).SequenceEqual([false, true]) &&
            requests.All(i => i.Message.EnumerateObject().Count() == 2),
            "display-power uses phone media with exact off/on values; application sessions ignore the request");
        await phone.DisposeAsync();
        Check(server.MediaInputs.Count(i => i.Message.GetProperty("c").GetString() == "display-power") == 2,
            "phone close uses close-window without an additional display-power request");
    }
}
