using System.Net;
using System.Net.WebSockets;
using Andromeld.Discovery;
using Andromeld.Protocol;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace Andromeld.Discovery.Tests;

internal static class Program
{
    private static int _checks;

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
        _checks++;
        Console.WriteLine($"PASS: {description}");
    }

    public static void Main(string[] args) => SingleThreadContext.Run(() => RunAsync(args));

    private static async Task RunAsync(string[] args)
    {
        if (args.Contains("--wireless"))
        {
            await WirelessPairingChecks.RunAsync(Check);
            Console.WriteLine($"{_checks} wireless ADB checks passed.");
            return;
        }
        if (args.Contains("--live-usb"))
        {
            await UsbLiveChecks.RunAsync(Check, args.Contains("--restart-agent"));
            Console.WriteLine($"{_checks} live USB checks passed.");
            return;
        }
        if (args.Contains("--live")) { await CheckLiveAsync(); return; }
        var catalog = new DiscoveryCatalog();
        string[] txt = ["n=测试手机", "p=12969", "id=0011223344556677", "v=1"];
        catalog.Update("_andromeld._tcp.", "AMDA-00112233", 12969, txt, [IPAddress.Parse("192.0.2.7")], "eth0", "Ethernet");
        Check(catalog.Snapshot() is [{ Name: "测试手机", Endpoints: [{ AgentPort: 12969 }] }], "TXT parsed by key; advertised native port stays separate");
        catalog.Update("_andromeld._tcp", "amda-00112233", 12969, txt, [IPAddress.Parse("192.0.2.8")], "wlan0", "Wi-Fi");
        Check(catalog.Snapshot() is [{ Endpoints.Count: 2 }], "one identity merges addresses from multiple interfaces");
        catalog.Remove("eth0", "AMDA-00112233");
        Check(catalog.Snapshot() is [{ Endpoints: [{ InterfaceId: "wlan0" }] }], "removing one interface preserves remaining route");
        catalog.Update("_andromeld._tcp", "AMDA-other", 12969, ["v=1", "id=8899aabbccddeeff"], [IPAddress.Parse("192.0.2.8")], "wlan0", "Wi-Fi");
        Check(catalog.Snapshot().Length == 2, "devices sharing an IP are not merged");
        catalog.Remove("wlan0", "AMDA-00112233");
        catalog.Remove("wlan0", "AMDA-other");
        Check(catalog.Snapshot().Length == 0, "goodbye/removal clears the final route");
        catalog.Update("_andromeld._tcp", "bad", 12969, ["v=1", "id=0011223344556677", "p=13012"], [IPAddress.Parse("192.0.2.9")], "eth0", "Ethernet");
        catalog.Update("_adb._tcp", "bad", 12969, txt, [IPAddress.Parse("192.0.2.9")], "eth0", "Ethernet");
        catalog.Update("_andromeld._tcp", "bad", 12969, txt, [IPAddress.Loopback, IPAddress.IPv6Loopback, IPAddress.Parse("224.0.0.251")], "eth0", "Ethernet");
        Check(catalog.Snapshot().Length == 0, "rejects mismatched ports, unrelated services and unusable addresses");
        using (var discovery = new LanDiscovery())
        {
            var rejected = await Task.Run(() =>
            {
                try { _ = discovery.Devices; return false; }
                catch (InvalidOperationException) { return true; }
            });
            Check(rejected, "discovery rejects access from outside its owning thread");
        }
        await CheckWebProbeAsync();
        await UsbChecks.RunAsync(Check);
        await WirelessPairingChecks.RunAsync(Check);
        await UsbPreparationChecks.RunAsync(Check);
        await WirelessChecks.RunAsync(Check);
        Console.WriteLine($"{_checks} discovery checks passed.");
    }

    private static async Task CheckWebProbeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        await using var app = builder.Build();
        app.UseWebSockets();
        var receivedClose = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        app.Map("/ws", async context =>
        {
            if (context.Request.Headers.Origin != AndromeldClient.Origin) { context.Response.StatusCode = 403; return; }
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var message = await socket.ReceiveAsync(new byte[512], context.RequestAborted);
            receivedClose.TrySetResult(message.MessageType == WebSocketMessageType.Close);
        });
        app.Map("/http-only", () => "not a websocket");
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var uri = new Uri(address.Replace("http://", "ws://", StringComparison.Ordinal) + "/ws");
        var device = new LanDevice("test", "test", "test", [new(IPAddress.Loopback, 12969, "test", "test")]);
        var found = await WebEndpointResolver.FindAsync(device, uri, CancellationToken.None);
        Check(found == uri && await receivedClose.Task.WaitAsync(TimeSpan.FromSeconds(3)), "Web port probe sends required Origin and no authentication payload");
        Check(!await WebEndpointResolver.ProbeAsync(new Uri(uri, "/http-only"), CancellationToken.None), "ordinary HTTP endpoint is not accepted as a Web connection");
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        try { await WebEndpointResolver.FindAsync(device, null, cancel.Token); throw new InvalidOperationException("Cancellation ignored"); }
        catch (OperationCanceledException) { Check(true, "Web endpoint resolution observes cancellation"); }
    }

    private static async Task CheckLiveAsync()
    {
        using var discovery = new LanDiscovery();
        discovery.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (discovery.Devices.Count == 0) await Task.Delay(100, timeout.Token);
        var device = discovery.Devices[0];
        Console.WriteLine($"Real mDNS: {device.Name}; {string.Join(", ", device.Endpoints.Select(ep => $"{ep.Address}:{ep.AgentPort} via {ep.InterfaceName}"))}");
        Check(device.Endpoints.All(ep => ep.AgentPort != 13012), "real mDNS retains native Agent port");
        var web = await WebEndpointResolver.FindAsync(device, null, timeout.Token);
        Check(web is not null, "real discovered device exposes a separately verified Web endpoint");
        Console.WriteLine($"Real Web endpoint: {web}");
        discovery.Refresh();
        while (discovery.Devices.Count == 0) await Task.Delay(100, timeout.Token);
        Check(discovery.Devices.Any(d => d.Key == device.Key), "refresh restarts browsing and rediscovers real device");
    }
}
