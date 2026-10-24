using System.Text;
using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.Receivers;
using Andromeld.Discovery;
using Andromeld.Protocol;

namespace Andromeld.Discovery.Tests;

/// <summary>Opt-in phone checks; requires an already running, authorized ADB server.</summary>
internal static class UsbLiveChecks
{
    public static async Task RunAsync(Action<bool, string> check, bool restartAgent)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var ct = stop.Token;
        var discovery = new UsbDiscovery();
        var devices = await discovery.FindAsync(ct);
        // Refuse to choose a phone implicitly when multiple USB devices are attached.
        check(devices is [{ State: UsbDeviceState.Online }], "exactly one authorized USB device is attached");
        var device = devices[0];
        var adb = new AdbClient();
        var selected = (await adb.GetDevicesAsync(ct)).Single(candidate => candidate.Serial == device.Serial);
        if (restartAgent)
        {
            var output = new ConsoleOutputReceiver();
            await adb.ExecuteRemoteCommandAsync(UsbHelper.StopAgentCommand, selected, output, Encoding.UTF8, ct);
            UsbHelper.CheckAgentStopped(output.ToString());
            check(true, "embedded stop script completes planned exit on the phone");
            output = new ConsoleOutputReceiver();
            await adb.ExecuteRemoteCommandAsync(UsbBootstrap.ShellCommand, selected, output, Encoding.UTF8, ct);
            try { UsbBootstrap.Parse(output.ToString()); throw new InvalidOperationException("Expected a stopped agent"); }
            catch (UsbBootstrapException ex) when (ex.ErrorCode is "agent_unavailable")
            {
                check(true, "phone bootstrap reports agent_unavailable before cold startup");
            }
        }

        int localPort;
        await using (var connection = await discovery.OpenAsync(device, ct, new ConsoleProgress()))
        {
            localPort = connection.Endpoint.Port;
            check(connection.Endpoint.IsLoopback, "production USB preparation starts the service, validates PID/state and allocates a forward");
            await using var client = new AndromeldClient();
            await client.ConnectUsbAsync(connection.Endpoint, "usb-live-check-" + Guid.NewGuid().ToString("N"), null, connection.Bootstrap, ct);
            check(!string.IsNullOrEmpty(client.Token), "real USB ticket authenticates over ClientWebSocket");
            var apps = await client.GetAppsAsync(ct);
            check(apps.Length > 0, "real USB app-list returns applications without saving phone data");
        }
        var forwards = await adb.ListForwardAsync(selected, ct);
        check(forwards.All(forward => forward.Local != $"tcp:{localPort}"), "disposing the real USB connection removes its own forward");
    }

    private sealed class ConsoleProgress : IProgress<UsbConnectionProgress>
    {
        public void Report(UsbConnectionProgress value) => Console.WriteLine($"USB stage: {value.Stage}; notice: {value.Notice}");
    }
}
