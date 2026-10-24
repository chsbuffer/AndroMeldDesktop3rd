using Andromeld.Discovery;
using Andromeld.Protocol;
using Andromeld.Testing;

namespace Andromeld.Discovery.Tests;

internal static class UsbChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = stop.Token;
        using var http = new HttpClient(new FakeUsbDownloads());
        await using var adb = new FakeAdbServer();
        adb.Devices += "WINDOWS-USB\tdevice product:test model:Windows_Phone device:test transport_id:2\n" +
            "UNAUTHORIZED\tunauthorized usb:1-3 transport_id:3\nOFFLINE\toffline usb:1-4 transport_id:4\n" +
            "192.0.2.1:5555\tdevice model:Wireless transport_id:5\nemulator-5554\tdevice transport_id:6\n" +
            "adb-test._adb-tls-connect._tcp\tdevice transport_id:7\n";
        var discovery = new UsbDiscovery(adb.Endpoint, httpClient: http);
        var devices = await discovery.FindAsync(ct);
        check(devices.Count == 4 && devices[1].Name == "Windows Phone" && devices[2].State == UsbDeviceState.Unauthorized &&
            devices[3].State == UsbDeviceState.Unavailable, "USB discovery includes Windows serials and authorization states, excludes TCP, wireless mDNS and emulators");
        await using (var connection = await discovery.OpenAsync(devices[1], ct))
        {
            check(connection.Bootstrap.WebPort == 13019 && connection.Endpoint.IsLoopback && connection.Endpoint.AbsolutePath == "/ws" &&
                adb.Requests.Contains("shell:" + UsbBootstrap.ShellCommand) &&
                adb.Requests.Contains("host-serial:WINDOWS-USB:forward:norebind:tcp:0;tcp:13019"),
                "selected USB serial bootstraps and forwards the returned phone port with an ADB-assigned local port");
        }
        check(adb.ForwardCount == 0 && adb.Requests.Count(request => request.Contains(":killforward:tcp:", StringComparison.Ordinal)) == 1,
            "disposing a USB connection removes only its own forward");
        var allocations = adb.Requests.Count(request => request.Contains(":forward:", StringComparison.Ordinal));
        adb.BootstrapOutput = "Result: Bundle[{status=error, errorCode=privacy_consent_required}]\n";
        try { await discovery.OpenAsync(devices[0], ct); throw new Exception("Expected provider error"); }
        catch (IOException ex) { check(ex.Message.Contains("隐私") && adb.Requests.Count(request => request.Contains(":forward:", StringComparison.Ordinal)) == allocations,
            "provider failure is actionable and never creates a forward"); }
        try { await discovery.OpenAsync(devices[2], ct); throw new Exception("Expected authorization error"); }
        catch (IOException ex) { check(ex.Message.Contains("USB 调试"), "unauthorized USB devices cannot invoke bootstrap"); }

        await using var rejected = new FakeAdbServer { RejectForward = true };
        try { await new UsbDiscovery(rejected.Endpoint, httpClient: http).OpenAsync(devices[0], ct); throw new Exception("Expected forward failure"); }
        catch (UsbConnectionException)
        {
            check(rejected.ForwardCount == 0, "ADB forward failure does not expose a usable USB connection");
        }

        await using var delayed = new FakeAdbServer { ForwardReplyGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var delayedDiscovery = new UsbDiscovery(delayed.Endpoint, httpClient: http);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var opening = delayedDiscovery.OpenAsync(new("USB-ONE", "Test Phone", UsbDeviceState.Online), cancel.Token);
        await delayed.ForwardRequested.Task.WaitAsync(ct);
        await cancel.CancelAsync();
        delayed.ForwardReplyGate.SetResult();
        try { await opening; throw new Exception("Expected cancellation"); }
        catch (OperationCanceledException)
        {
            check(delayed.ForwardCount == 0 && delayed.Requests.Any(request => request.Contains(":killforward:tcp:", StringComparison.Ordinal)),
                "cancellation during ADB allocation waits for the assigned port and removes the resulting forward");
        }
    }
}
