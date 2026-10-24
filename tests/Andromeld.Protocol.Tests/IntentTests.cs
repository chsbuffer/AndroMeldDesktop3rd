using Andromeld.Protocol;
using Andromeld.Testing;

namespace Andromeld.Protocol.Tests;

public static partial class Program
{
    private static async Task CheckIntentsAsync()
    {
        foreach (var text in new[] { "https://example.com/a?q=%E4%B8%AD#part", "http://localhost:8080/", "HTTPS://example.com", "https:example.com", "https:/example.com", "https://example.com:0", "tel:+123456", "mailto:test@example.com", "geo:31.2,121.5" })
            Check(AndroidIntentUri.TryParse(" " + text + " ", out var intent) && intent.Value == text, "Web intent keeps the original target: " + text);
        foreach (var text in new[] { "example.com/path", "example.madeup", "a.xn--fiqs8s", "example.com:8443/a" })
            Check(AndroidIntentUri.TryParse(text, out var intent) && intent.Value == "https://" + text, "Web bare-domain syntax adds HTTPS: " + text);
        foreach (var text in new[] { "", "app", "custom://path", "intent://x#Intent;end", "file:///tmp/a", "https://", "https://exa mple.com", "https://999.999", "tel:", "a.c", "a.123", "example.com:0", "example.com:65536", "example.com\npath", "https://example.com/" + new string('x', 8192) })
            Check(!AndroidIntentUri.TryParse(text, out _), "non-link text remains a clipboard candidate: " + text[..Math.Min(text.Length, 70)]);

        await using var server = await MockPhone.StartAsync();
        await using var client = new AndromeldClient();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(server.Endpoint, "intent-tests", null, null, stop.Token);
        client.Capabilities.Remove("phone-screen");
        AndroidIntentUri.TryParse("example.com/deep/path?x=1", out var target);
        await using (var session = await client.OpenIntentMirrorAsync(target!, stop.Token, 750, 1200, 1.5))
        {
            var request = server.OpenedMirrors.Single();
            Check(!session.State.IsPhone && request.GetProperty("intentUrl").GetString() == target!.Value &&
                !request.TryGetProperty("pkg", out _) && !request.TryGetProperty("userId", out _) && !request.TryGetProperty("kind", out _) &&
                request.GetProperty("w").GetInt32() == 750 && request.GetProperty("h").GetInt32() == 1200 && request.GetProperty("dpr").GetDouble() == 1.5,
                "intent uses measured application dimensions without an app/user/phone target or phone-screen capability");
            await session.AttachAsync(stop.Token);
        }
        server.IntentOpenFailure = "invalid-intent-url";
        try { await client.OpenIntentMirrorAsync(target!, stop.Token); throw new Exception("Expected intent failure"); }
        catch (IOException ex) { Check(ex.Message.Contains("链接"), "empty-package intent failure is routed to its opening request"); }
        server.IntentOpenFailure = null;
        server.OpenDelayMs = 150;
        using var canceled = new CancellationTokenSource();
        var opening = client.OpenIntentMirrorAsync(target!, canceled.Token);
        await UntilAsync(() => server.OpenedMirrors.Count == 3, stop.Token);
        canceled.Cancel();
        try { await opening; throw new Exception("Expected cancellation"); }
        catch (OperationCanceledException) { Check(true, "canceled intent open awaits the late session and releases it"); }
        await UntilAsync(() => server.ClosedMirrors.Count == 2, stop.Token);
    }
}
