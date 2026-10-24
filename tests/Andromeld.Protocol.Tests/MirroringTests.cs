using Andromeld.Protocol;
using Andromeld.Testing;

namespace Andromeld.Protocol.Tests;

public static partial class Program
{
    private static async Task CheckMirroringAsync(AndromeldClient client, MockPhone server, PhoneApp app)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var sessions = await Task.WhenAll(client.OpenMirrorAsync(null, stop.Token), client.OpenMirrorAsync(app, stop.Token, 1440, 810, 2));
        await using var phone = sessions[0];
        await using var application = sessions[1];
        Check(phone.State.IsPhone && !application.State.IsPhone && phone.SessionId != application.SessionId, "concurrent phone and application opens receive separate session IDs");
        var open = server.OpenedMirrors.Single(r => r.TryGetProperty("pkg", out _));
        Check(open.GetProperty("w").GetInt32() == 1440 && open.GetProperty("h").GetInt32() == 810 && open.GetProperty("dpr").GetDouble() == 2,
            "initial application open preserves measured landscape dimensions and DPI");
        var video = new System.Collections.Concurrent.ConcurrentQueue<VideoPacket>();
        var configurations = new System.Collections.Concurrent.ConcurrentQueue<VideoConfiguration>();
        foreach (var session in sessions)
        {
            session.ConfigureVideo = config => { configurations.Enqueue(config); return ValueTask.CompletedTask; };
            session.ReceiveVideo = packet => { video.Enqueue(packet); return ValueTask.CompletedTask; };
        }
        await Task.WhenAll(phone.AttachAsync(stop.Token), application.AttachAsync(stop.Token));
        await UntilAsync(() => server.VideoAcks.Count == 8 && video.Count == 4, stop.Token);
        Check(configurations.Count == 2 && video.All(p => p.IsKeyFrame && p.TimestampUs == 123456 && p.Data.Span.StartsWith(new byte[] { 0, 0, 0, 1 })), "fragmented media frames parse BE timestamps and Annex B data, discard delta before keyframe and after gap");
        Check(server.VideoAcks.Any(a => a.Bytes == MockPhone.VideoCounter) && server.VideoAcks.GroupBy(a => a.SessionId).All(g => g.Count() == 4), "ACK uses exact uint64 cumulative counter on control socket, including discarded frames");
        Check(server.KeyFrameRequests >= 2, "stream gaps request a replacement keyframe");
        await phone.PointerAsync("down", 50, 60, stop.Token);
        await phone.PointerAsync("up", 50, 60, stop.Token);
        await application.TextAsync("镜像输入", stop.Token);
        await application.KeyAsync(4, 0, stop.Token);
        await application.ScrollAsync(30, 40, 0, -2, stop.Token);
        await application.ResizeAsync(MirrorSize.FromViewport(720, 405, 2), stop.Token);
        await phone.ResizeAsync(new MirrorSize(1000, 500, 1), stop.Token);
        await UntilAsync(() => server.MediaInputs.Count(i => i.Message.GetProperty("c").GetString() == "input") == 6, stop.Token);
        Check(server.MediaInputs.Any(i => i.SessionId == phone.SessionId && i.Message.TryGetProperty("a", out var a) && a.GetString() == "down") &&
            server.MediaInputs.Any(i => i.SessionId == application.SessionId && i.Message.TryGetProperty("text", out var text) && text.GetString() == "镜像输入"), "pointer, Unicode text, key and scroll input route to their own media sockets");
        await UntilAsync(() => server.MediaInputs.Any(i => i.Message.GetProperty("c").GetString() == "resize"), stop.Token);
        var resize = server.MediaInputs.Single(i => i.Message.GetProperty("c").GetString() == "resize");
        Check(resize.SessionId == application.SessionId && resize.Message.GetProperty("w").GetInt32() == 1440 && resize.Message.GetProperty("h").GetInt32() == 810 && resize.Message.GetProperty("dpr").GetDouble() == 2,
            "resize carries physical viewport dimensions and DPI on application media only");
        var capped = MirrorSize.FromViewport(2000, 1000, 2);
        Check(capped.Width == 1920 && capped.Height == 960 && Math.Abs(capped.Dpr - .96) < .000001,
            "encoding limit proportionally caps dimensions and density");
        foreach (var (width, height, dpr, zoom, compatible, expectedWidth, expectedHeight, scale) in new[]
        {
            (480, 800, 1d, 100, false, 480, 800, .8),
            (480, 800, 1d, 110, false, 480, 800, .88),
            (300, 500, 1d, 100, false, 300, 500, .666),
            (480, 800, 1d, 100, true, 392, 652, .8),
            (800, 480, 1d, 100, true, 652, 392, .8),
            (480, 800, 1.5, 100, true, 588, 980, .8)
        })
        {
            var actual = MirrorDisplayConfiguration.FromViewport(MirrorSize.FromViewport(width, height, dpr), zoom, compatible);
            Check(actual == new MirrorDisplayConfiguration(new(expectedWidth, expectedHeight, dpr), scale),
                $"Web DISPLAY vector {width}×{height}@{dpr}, {zoom}%, compatible={compatible}");
        }
        Check(MirrorSize.FromViewport(481.5, 800, 1).Width == 482,
            "half-pixel rounding matches JavaScript before even-pixel normalization");
        var displayConfiguration = MirrorDisplayConfiguration.FromViewport(new(480, 800, 1), 110, true);
        var beforeDisplay = server.MediaInputs.Count;
        await application.ConfigureDisplayAsync(displayConfiguration, stop.Token);
        await application.ConfigureDisplayAsync(displayConfiguration, stop.Token);
        await phone.ConfigureDisplayAsync(displayConfiguration, stop.Token);
        await UntilAsync(() => server.MediaInputs.Count >= beforeDisplay + 3, stop.Token);
        var displayMessages = server.MediaInputs.Skip(beforeDisplay).ToArray();
        Check(displayMessages.Select(i => i.Message.GetProperty("c").GetString()).SequenceEqual(["density", "resize", "resize"]) &&
            displayMessages.All(i => i.SessionId == application.SessionId) &&
            displayMessages[0].Message.GetProperty("scale").GetDouble() == .88 &&
            displayMessages[1].Message.GetProperty("w").GetInt32() == 392 && displayMessages[1].Message.GetProperty("h").GetInt32() == 652,
            "display sends density before resize, deduplicates density, and ignores physical phone configuration");
        try
        {
            await application.ConfigureDisplayAsync(displayConfiguration with { Scale = double.NaN }, stop.Token);
            throw new Exception("Invalid density accepted");
        }
        catch (ArgumentOutOfRangeException) { Check(true, "non-finite density is rejected before media send"); }
        var edge = MirrorDisplayConfiguration.FromViewport(MirrorSize.FromViewport(480, 800, 1.5), 110, false, 32);
        var compatibleEdge = MirrorDisplayConfiguration.FromViewport(MirrorSize.FromViewport(480, 800, 1.5), 150, true, 32);
        var cappedEdge = MirrorDisplayConfiguration.FromViewport(capped, 100, false, 32);
        Check(edge.DecorationTop == 48 && compatibleEdge.DecorationTop == 48 && cappedEdge.DecorationTop == 31,
            "edge-to-edge insets follow effective DPR including encoding caps, independently of zoom and compatibility");
        beforeDisplay = server.MediaInputs.Count;
        await application.ConfigureDisplayAsync(edge, stop.Token);
        await application.ConfigureDisplayAsync(edge, stop.Token);
        await application.ConfigureDisplayAsync(edge with { DecorationTop = 0 }, stop.Token);
        await phone.ConfigureDisplayAsync(edge, stop.Token);
        await UntilAsync(() => server.MediaInputs.Count >= beforeDisplay + 5, stop.Token);
        displayMessages = server.MediaInputs.Skip(beforeDisplay).ToArray();
        Check(displayMessages.Select(i => i.Message.GetProperty("c").GetString()).SequenceEqual(["resize", "decor-insets", "resize", "resize", "decor-insets"]) &&
            displayMessages.All(i => i.SessionId == application.SessionId) &&
            displayMessages[1].Message.GetProperty("top").GetInt32() == 48 && displayMessages[^1].Message.GetProperty("top").GetInt32() == 0,
            "edge-to-edge sends resize then changed insets, deduplicates unchanged insets and restores zero on fullscreen");
        beforeDisplay = server.MediaInputs.Count;
        try
        {
            await application.ConfigureDisplayAsync(edge with { DecorationTop = -1 }, stop.Token);
            throw new Exception("Invalid inset accepted");
        }
        catch (ArgumentOutOfRangeException)
        {
            Check(server.MediaInputs.Count == beforeDisplay, "negative decoration insets are rejected before media send");
        }
        await phone.DisposeAsync();
        await UntilAsync(() => server.ClosedMirrors.Contains(phone.SessionId), stop.Token);
        Check(!server.ClosedMirrors.Contains(application.SessionId), "closing the phone releases only its session");
        await application.DisposeAsync();
        await UntilAsync(() => server.ClosedMirrors.Contains(application.SessionId), stop.Token);
        server.OpenDelayMs = 80;
        var closed = server.ClosedMirrors.Count;
        using var cancel = new CancellationTokenSource(20);
        try { await client.OpenMirrorAsync(null, cancel.Token); throw new Exception("Cancellation ignored"); }
        catch (OperationCanceledException) { }
        await UntilAsync(() => server.ClosedMirrors.Count > closed, stop.Token);
        Check(true, "closing while opening still releases the eventual device-side session");
        server.OpenDelayMs = 0;
        try { await client.OpenMirrorAsync(app with { PackageName = "test.fail" }, stop.Token); throw new Exception("Pool failure ignored"); }
        catch (IOException ex) { Check(ex.Message.Contains("已满"), "pool-full surfaces a useful window error"); }
    }

    private static async Task UntilAsync(Func<bool> predicate, CancellationToken ct)
    {
        while (!predicate()) await Task.Delay(10, ct);
    }
}
