using System.Text.Json;
using Andromeld.Desktop.Services;
using Andromeld.Desktop.ViewModels;
using Andromeld.Desktop.Views;
using Andromeld.Discovery;
using Andromeld.Protocol;
using Andromeld.Testing;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using ReactiveUI.Primitives;

namespace Andromeld.Desktop.Tests;

internal static class UsbChecks
{
    public static async Task RunAsync(string temporary, CancellationToken ct)
    {
        await using var phone = await MockPhone.StartAsync("usb-synthetic");
        await using var adb = new FakeAdbServer(phone.Endpoint.Port);
        using var http = new HttpClient(new FakeUsbDownloads());
        var settings = new Settings { StoragePath = Path.Combine(temporary, "usb", "settings.json"), Address = "unchanged.invalid:13012", AudioForwardingEnabled = false, ClipboardSyncEnabled = false };
        await using var model = new MainViewModel(settings, new StubDiscovery(), usbDiscovery: new UsbDiscovery(adb.Endpoint, httpClient: http)) { Code = "not-a-connect-code" };
        var window = new MainWindow { DataContext = model };
        window.Show();
        try
        {
            await model.RefreshUsbDevicesCommand.Execute().ToTask();
            window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            var cards = window.FindControl<DeviceConnectionView>("DevicePage")!.FindControl<ItemsControl>("UsbDeviceCards")!;
            var card = cards.GetVisualDescendants().OfType<Button>().Single(button => button.Classes.Contains("nearbyDevice"));
            card.Command!.Execute(card.CommandParameter);
            await UntilAsync(() => model.IsConnected && !model.IsConnecting && model.Apps.Count == 18 && model.FileBrowser.VisibleFiles.Count == 201, ct);
            var firstPort = adb.Requests.Single(request => request.Contains(":forward:", StringComparison.Ordinal));
            Program.Check(model.ConnectionTransport == "USB" && model.Address == "unchanged.invalid:13012" && model.Code == "not-a-connect-code" &&
                phone.AuthenticationTypes.SequenceEqual(["bootstrap"]) && phone.BootstrapTickets.Single() == "39U6DY5U" &&
                settings.Tokens.Keys.Single().StartsWith("usb:", StringComparison.Ordinal),
                "USB card bootstraps over an actual ADB TCP forward, loads apps/files and stores credentials independently of the local port or manual/code input");

            await model.OpenPhoneCommand.Execute().ToTask();
            await UntilAsync(() => model.Mirrors.Single().HasFrame, ct);
            Program.Check(adb.ForwardCount == 1 && adb.ActiveStreams >= 2, "control and independently attached media ClientWebSockets share the USB forward");
            await model.DisconnectCommand.Execute().ToTask();
            await UntilAsync(() => adb.ActiveStreams == 0, ct);
            Program.Check(adb.ForwardCount == 0 && model.Mirrors.Count == 0 && model.CanConnect, "USB disconnect closes mirror/control sockets and removes the forward before enabling another connection");

            await model.ConnectUsbCommand.Execute(model.UsbDevices.Single()).ToTask();
            Program.Check(phone.AuthenticationTypes.SequenceEqual(["bootstrap", "resume"]) && model.IsConnected && adb.ForwardCount == 1 &&
                adb.Requests.Count(request => request == firstPort) == 2, "USB reconnect resumes the per-device credential through a newly allocated local port");
            await model.DisconnectCommand.Execute().ToTask();
            settings.Tokens[settings.Tokens.Keys.Single()] = "expired-token";
            await model.ConnectUsbCommand.Execute(model.UsbDevices.Single()).ToTask();
            Program.Check(phone.AuthenticationTypes.TakeLast(2).SequenceEqual(["resume", "bootstrap"]) && model.IsConnected &&
                JsonSerializer.Deserialize<Settings>(File.ReadAllText(settings.StoragePath))!.Tokens.Values.Single() == "test-token",
                "revoked USB credentials retry once with the provider ticket on a fresh socket and save the replacement");
            try { await model.FileBrowser.NavigateAsync("/storage/emulated/0/drop"); throw new Exception("Expected connection drop"); }
            catch (IOException) { }
            await UntilAsync(() => !model.IsConnected && !model.IsDisconnecting && adb.ForwardCount == 0, ct);
            Program.Check(model.CanSelectNearbyDevice && model.Status == "连接已断开", "an unexpected USB control disconnect releases its forward and permits reconnection");

            phone.PairDenialReason = "DENIED";
            try { await model.RePairUsbCommand.Execute(model.UsbDevices.Single()).ToTask(); throw new Exception("Expected authentication denial"); }
            catch (AuthenticationDeniedException) { }
            await UntilAsync(() => model.HasError, ct);
            Program.Check(adb.ForwardCount == 0 && !model.IsConnected && !model.IsConnecting, "USB authentication failure removes the forward and leaves the error visible");
            phone.PairDenialReason = null;

            adb.ShellReplyGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = model.ConnectUsbCommand.Execute(model.UsbDevices.Single()).ToTask();
            await UntilAsync(() => model.IsConnecting && !model.CanSelectNearbyDevice, ct);
            await model.CancelConnectCommand.Execute().ToTask();
            await pending;
            adb.ShellReplyGate.SetResult();
            Program.Check(adb.ForwardCount == 0 && !model.IsConnecting && !model.IsConnected && model.CanConnect,
                "canceling USB initialization restores connection actions without leaving a forward");
        }
        finally
        {
            adb.ShellReplyGate?.TrySetResult();
            window.DataContext = null; window.Close();
        }

        await using var closingAdb = new FakeAdbServer(phone.Endpoint.Port) { ForwardReplyGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var closingModel = new MainViewModel(new Settings { StoragePath = Path.Combine(temporary, "usb-closing", "settings.json") },
            new StubDiscovery(), usbDiscovery: new UsbDiscovery(closingAdb.Endpoint, httpClient: http));
        await closingModel.RefreshUsbDevicesCommand.Execute().ToTask();
        var connecting = closingModel.ConnectUsbCommand.Execute(closingModel.UsbDevices.Single()).ToTask();
        await closingAdb.ForwardRequested.Task.WaitAsync(ct);
        var closing = closingModel.DisposeAsync().AsTask();
        Program.Check(!closing.IsCompleted, "UI owner disposal waits for in-flight USB forward allocation");
        closingAdb.ForwardReplyGate.SetResult();
        await Task.WhenAll(connecting, closing);
        Program.Check(closingAdb.ForwardCount == 0 && !closingModel.IsConnected, "disposing during USB initialization cancels authentication and releases the allocated forward");
        await UsbPreparationUiChecks.RunAsync(temporary, ct);
        await WirelessChecks.RunAsync(temporary, ct);
    }

    private static async Task UntilAsync(Func<bool> condition, CancellationToken ct)
    {
        while (!condition()) await Task.Delay(10, ct);
    }
}
