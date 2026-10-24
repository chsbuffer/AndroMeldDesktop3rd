using Andromeld.Desktop.Services;
using Andromeld.Desktop.ViewModels;
using Andromeld.Desktop.Views;
using Andromeld.Discovery;
using Andromeld.Testing;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using ReactiveUI.Primitives;

namespace Andromeld.Desktop.Tests;

internal static class WirelessChecks
{
    private const string Address = "192.0.2.55";
    private const string Services = "adb-synthetic _adb-tls-connect._tcp 192.0.2.55:41000\n" +
                                    "adb-synthetic _adb-tls-pairing._tcp 192.0.2.55:42000\n";

    public static async Task RunAsync(string temporary, CancellationToken ct)
    {
        using var http = new HttpClient(new FakeUsbDownloads());
        await CheckPairingAsync(temporary, http, ct);
        await CheckCancellationAsync(temporary, http, ct);
    }

    private static async Task CheckPairingAsync(string temporary, HttpClient http, CancellationToken ct)
    {
        await using var phone = await MockPhone.StartAsync("wireless-ip");
        await using var adb = new FakeAdbServer(phone.Endpoint.Port)
        {
            MdnsServices = Services,
            RequireWirelessPairing = true,
            WirelessPairingCode = "135790"
        };
        var settings = new Settings
        {
            StoragePath = Path.Combine(temporary, "wireless", "settings.json"),
            Address = Address,
            AudioForwardingEnabled = false,
            ClipboardSyncEnabled = false
        };
        await using var model = new MainViewModel(settings, new StubDiscovery(), usbDiscovery: new UsbDiscovery(adb.Endpoint, httpClient: http));
        var window = new MainWindow { DataContext = model };
        window.Show();
        try
        {
            var page = window.FindControl<DeviceConnectionView>("DevicePage")!;
            Task connecting = model.ConnectCommand.Execute().ToTask();
            await UntilAsync(() => model.IsWaitingForWirelessPairing, ct);
            window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            var box = page.FindControl<TextBox>("WirelessPairingCodeBox")!;
            Program.Check(box.IsEffectivelyVisible && !((System.Windows.Input.ICommand)model.SubmitWirelessPairingCommand).CanExecute(null),
                "wireless debugging pairing reveals an empty six-digit code prompt without a premature submit");

            model.WirelessPairingCode = "135790";
            await model.SubmitWirelessPairingCommand.Execute().ToTask();
            await connecting;
            await UntilAsync(() => model.IsConnected && model.Apps.Count == 18 && model.FileBrowser.VisibleFiles.Count == 201, ct);

            Program.Check(model.ConnectionTransport == "无线调试" && adb.ForwardCount == 1 &&
                adb.ConnectRequests.SequenceEqual([$"{Address}:41000", $"{Address}:41000"]) &&
                adb.PairRequests.Single() == $"135790@{Address}:42000" &&
                phone.AuthenticationTypes.SequenceEqual(["bootstrap"]) &&
                settings.Tokens.Keys.Single().StartsWith("wireless:", StringComparison.Ordinal),
                "an IP-only connection without a Web service pairs wireless ADB, then bootstraps and stores a separate credential");

            await model.DisconnectCommand.Execute().ToTask();
            await UntilAsync(() => adb.ForwardCount == 0, ct);
            Program.Check(!model.IsConnected && model.CanConnect, "wireless ADB disconnect removes its forward and permits reconnection");
        }
        finally
        {
            window.DataContext = null;
            window.Close();
        }
    }

    private static async Task CheckCancellationAsync(string temporary, HttpClient http, CancellationToken ct)
    {
        await using var adb = new FakeAdbServer
        {
            MdnsServices = Services,
            RequireWirelessPairing = true
        };
        await using var model = new MainViewModel(
            new Settings { StoragePath = Path.Combine(temporary, "wireless-cancel", "settings.json"), Address = Address },
            new StubDiscovery(), usbDiscovery: new UsbDiscovery(adb.Endpoint, httpClient: http));
        Task pending = model.ConnectCommand.Execute().ToTask();
        await UntilAsync(() => model.IsWaitingForWirelessPairing, ct);
        await model.CancelConnectCommand.Execute().ToTask();
        await pending;

        Program.Check(!model.IsWaitingForWirelessPairing && !model.IsConnected && model.CanConnect &&
            model.Status == "已取消连接" && adb.ForwardCount == 0,
            "canceling the wireless pairing prompt clears UI state and leaves no ADB forward");
    }

    private static async Task UntilAsync(Func<bool> condition, CancellationToken ct)
    {
        while (!condition())
        {
            await Task.Delay(10, ct);
        }
    }
}
