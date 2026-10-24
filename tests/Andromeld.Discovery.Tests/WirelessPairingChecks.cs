using System.Net;

using Andromeld.Protocol;
using Andromeld.Testing;

namespace Andromeld.Discovery.Tests;

internal static class WirelessPairingChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var code = AdbPairingCode.Create();
        check(code.QrData == $"WIFI:T:ADB;S:{code.ServiceName};P:{code.Password};;" &&
            code.ServiceName.StartsWith("studio-", StringComparison.Ordinal) && code.Password.Length >= 10 &&
            code.QrData != AdbPairingCode.Create().QrData && !code.ToString().Contains(code.Password, StringComparison.Ordinal),
            "QR uses Android's ADB format with fresh ephemeral credentials and does not print its password");
        var services = AdbMdns.Parse("List of discovered mdns services\n" +
            "unrelated _adb._tcp 192.0.2.7:5555\ninvalid _adb-tls-connect._tcp broken\n" +
            "pair\t_adb-tls-pairing._tcp.\t192.0.2.7:37001\nconnect _adb-tls-connect._tcp [2001:db8::7]:37002\n");
        check(services.Count == 2 && services[0].Port == 37001 && services[1].Address.Equals(IPAddress.Parse("2001:db8::7")),
            "ADB mDNS parsing accepts advertised pairing/connect endpoints and ignores headers, other services and invalid addresses");

        await using var adb = new FakeAdbServer
        {
            WirelessPairingCode = code.Password,
            MdnsServices = $"other _adb-tls-pairing._tcp 192.0.2.8:37001\n{code.ServiceName} _adb-tls-pairing._tcp 192.0.2.7:37001\n" +
                "wrong-phone _adb-tls-connect._tcp 192.0.2.8:37002\nadb-test _adb-tls-connect._tcp 192.0.2.7:37002\n",
            Devices = "USB-OTHER\tdevice usb:1-2 model:Other\nadb-test._adb-tls-connect._tcp\tdevice model:Wireless_Phone\n"
        };
        var pairing = new WirelessAdbPairing(adb.Endpoint);
        var device = await pairing.PairAsync(code, stop.Token);
        check(device is { Serial: "adb-test._adb-tls-connect._tcp", Name: "Wireless Phone", IsWireless: true } &&
            adb.Requests.Contains($"host:pair:{code.Password}:192.0.2.7:37001") && adb.Requests.Contains("host:connect:192.0.2.7:37002") &&
            !adb.Requests.Any(request => request.Contains("192.0.2.8", StringComparison.Ordinal)),
            "pairing selects only this QR's service and connects the matching phone's debugging port instead of the pairing port");
        using var http = new HttpClient(new FakeUsbDownloads());
        await using (var connection = await new UsbDiscovery(adb.Endpoint, httpClient: http).OpenAsync(device, stop.Token))
        {
            check(connection.Endpoint.IsLoopback && adb.Requests.Contains("host-serial:adb-test._adb-tls-connect._tcp:forward:norebind:tcp:0;tcp:13019"),
                "the paired wireless serial reuses helper/bootstrap preparation and owns a local forward");
        }
        check(adb.ForwardCount == 0, "wireless ADB forward is removed on disposal");

        adb.PairingReply = "Failed: Wrong password or connection was dropped.";
        var connections = adb.Requests.Count(request => request.StartsWith("host:connect:", StringComparison.Ordinal));
        try { await pairing.PairAsync(code, stop.Token); throw new Exception("Expected pairing rejection"); }
        catch (IOException)
        {
            check(adb.Requests.Count(request => request.StartsWith("host:connect:", StringComparison.Ordinal)) == connections,
                "ADB's textual pairing rejection cannot proceed as a successful pair");
        }

        await using var waiting = new FakeAdbServer { MdnsServices = "someone-else _adb-tls-pairing._tcp 192.0.2.8:37001\n" };
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        var pending = new WirelessAdbPairing(waiting.Endpoint).PairAsync(code, cancel.Token);
        await waiting.MdnsRequested.Task.WaitAsync(stop.Token);
        await cancel.CancelAsync();
        try { await pending; throw new Exception("Expected cancellation"); }
        catch (OperationCanceledException)
        {
            check(!waiting.Requests.Any(request => request.StartsWith("host:pair:", StringComparison.Ordinal)),
                "canceling QR discovery exits without pairing a service belonging to another session");
        }

        await using var blocked = new FakeAdbServer
        {
            WirelessPairingCode = code.Password,
            MdnsServices = $"{code.ServiceName} _adb-tls-pairing._tcp 192.0.2.7:37001\n",
            PairingReplyGate = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        using var cancelPair = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        var blockedPair = new WirelessAdbPairing(blocked.Endpoint).PairAsync(code, cancelPair.Token);
        await blocked.PairingRequested.Task.WaitAsync(stop.Token);
        await cancelPair.CancelAsync();
        try { await blockedPair; throw new Exception("Expected cancellation during pair"); }
        catch (OperationCanceledException) { check(true, "canceling an in-flight pairing response closes the attempt promptly"); }
        blocked.PairingReplyGate.SetResult();
    }
}
