using Andromeld.Desktop.Services;
using Andromeld.Desktop.ViewModels;
using Andromeld.Desktop.Views;
using Andromeld.Discovery;
using Andromeld.Protocol;
using Andromeld.Testing;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Labs.Qr;
using Avalonia.Media.Imaging;
using Avalonia.Styling;

using ReactiveUI.Primitives;

namespace Andromeld.Desktop.Tests;

internal static class WirelessPairingChecks
{
    public static async Task RunAsync(string temporary, CancellationToken ct)
    {
        await using var phone = await MockPhone.StartAsync("wireless-synthetic");
        await using var adb = new FakeAdbServer(phone.Endpoint.Port)
        {
            Devices = "adb-test._adb-tls-connect._tcp\tdevice model:Wireless_Phone\n"
        };
        using var http = new HttpClient(new FakeUsbDownloads());
        var pairing = new ControlledPairing();
        var settings = new Settings
        {
            StoragePath = Path.Combine(temporary, "wireless", "settings.json"), Address = "unchanged.invalid:13012",
            AudioForwardingEnabled = false, ClipboardSyncEnabled = false
        };
        await using var model = new MainViewModel(settings, new StubDiscovery(), usbDiscovery: new UsbDiscovery(adb.Endpoint, httpClient: http),
            wirelessAdbPairing: pairing);
        var window = new MainWindow { DataContext = model };
        window.Show();
        var device = new UsbDevice("adb-test._adb-tls-connect._tcp", "Wireless Phone", UsbDeviceState.Online, IsWireless: true);
        try
        {
            await model.ShowQrConnectionCommand.Execute().ToTask();
            var page = window.FindControl<DeviceConnectionView>("DevicePage")!;
            window.UpdateLayout();
            Program.Check(pairing.Attempts.Count == 1 && page.FindControl<StackPanel>("QrConnection")!.IsEffectivelyVisible &&
                !page.FindControl<StackPanel>("ManualConnection")!.IsEffectivelyVisible &&
                page.FindControl<QrCode>("PairingQrCode")!.Data == pairing.Attempts[0].Code.QrData,
                "QR segment starts pairing and binds the actual session payload while hiding manual IP input");
            await SavePreviewAsync(window, page, "light", ct);
            window.RequestedThemeVariant = ThemeVariant.Dark;
            window.Width = 360;
            await SavePreviewAsync(window, page, "dark-narrow", ct);
            window.Width = 420;
            window.RequestedThemeVariant = ThemeVariant.Light;

            var first = pairing.Attempts[0];
            await model.RegeneratePairingQrCommand.Execute().ToTask();
            Program.Check(first.Token.IsCancellationRequested && pairing.Attempts.Count == 1 && !model.HasQrCode,
                "regeneration cancels and waits for the old attempt before advertising replacement credentials");
            first.Completion.SetResult(device);
            await UntilAsync(() => pairing.Attempts.Count == 2, ct);
            Program.Check(pairing.Attempts[1].Code.QrData != first.Code.QrData && !model.IsConnected && adb.ForwardCount == 0,
                "a late canceled result cannot connect a phone, and the next QR has fresh credentials");

            await model.ShowSettingsCommand.Execute().ToTask();
            Program.Check(pairing.Attempts[1].Token.IsCancellationRequested && !model.HasQrCode,
                "leaving the device page cancels wireless pairing and clears the QR");
            pairing.Attempts[1].Completion.SetResult(device);
            await model.ShowDeviceCommand.Execute().ToTask();
            await UntilAsync(() => pairing.Attempts.Count == 3, ct);
            await model.ShowIpConnectionCommand.Execute().ToTask();
            pairing.Attempts[2].Completion.SetResult(device);
            window.UpdateLayout();
            Program.Check(!model.HasQrCode && model.IsIpConnection && page.FindControl<StackPanel>("ManualConnection")!.IsEffectivelyVisible,
                "switching to IP retires QR pairing and restores the existing address controls");

            await model.ShowQrConnectionCommand.Execute().ToTask();
            await UntilAsync(() => pairing.Attempts.Count == 4, ct);
            phone.DeviceInformationReplyGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            pairing.Attempts[3].Completion.SetResult(device);
            await UntilAsync(() => model.IsConnected, ct);
            await model.ShowApplicationsCommand.Execute().ToTask();
            phone.DeviceInformationReplyGate.SetResult();
            await UntilAsync(() => model.IsConnected && !model.IsConnecting && model.Apps.Count == 18 && model.FileBrowser.VisibleFiles.Count == 201, ct);
            Program.Check(model.ConnectionTransport == "Wi-Fi" && model.Address == "unchanged.invalid:13012" &&
                phone.AuthenticationTypes.SequenceEqual(["bootstrap"]) && settings.Tokens.Keys.Single().StartsWith("adb:", StringComparison.Ordinal) &&
                adb.ForwardCount == 1 && !model.HasQrCode,
                "a scanned phone automatically bootstraps AndroMeld, loads apps/files and uses independent wireless credentials");
            Program.Check(model.IsApplicationsPage && model.IsConnected && !model.HasError,
                "after authentication, switching to applications while initial data loads preserves the established connection");

            await model.ShowIpConnectionCommand.Execute().ToTask();
            await model.DisconnectCommand.Execute().ToTask();
            Program.Check(adb.ForwardCount == 0, "disconnect removes the wireless phone's local ADB forward");

            await model.ShowDeviceCommand.Execute().ToTask();
            await model.ShowQrConnectionCommand.Execute().ToTask();
            await UntilAsync(() => pairing.Attempts.Count == 5, ct);
            adb.ForwardReplyGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            pairing.Attempts[4].Completion.SetResult(device);
            await UntilAsync(() => model.IsConnecting && adb.ForwardCount == 1, ct);
            await model.ShowSettingsCommand.Execute().ToTask();
            Program.Check(model.IsConnecting, "leaving QR initialization waits for the in-flight forward allocation to finish");
            adb.ForwardReplyGate.SetResult();
            await UntilAsync(() => !model.IsConnecting && adb.ForwardCount == 0, ct);
            Program.Check(!model.IsConnected && !model.HasError,
                "leaving before authentication cancels initialization and removes its eventual forward without a late error");
            adb.ForwardReplyGate = null;

            await model.ShowDeviceCommand.Execute().ToTask();
            await UntilAsync(() => pairing.Attempts.Count == 6, ct);
            var disposing = model.DisposeAsync().AsTask();
            Program.Check(!disposing.IsCompleted && pairing.Attempts[5].Token.IsCancellationRequested,
                "disposing the UI owner cancels and waits for its outstanding pairing operation");
            pairing.Attempts[5].Completion.SetResult(device);
            await disposing;
            Program.Check(!model.HasQrCode && !model.IsConnected && adb.ForwardCount == 0,
                "an outstanding result cannot revive a disposed UI owner or leak a wireless forward");
        }
        finally
        {
            phone.DeviceInformationReplyGate?.TrySetResult();
            adb.ForwardReplyGate?.TrySetResult();
            foreach (var attempt in pairing.Attempts) attempt.Completion.TrySetCanceled(ct);
            window.DataContext = null;
            window.Close();
        }
    }

    private static async Task SavePreviewAsync(MainWindow window, DeviceConnectionView page, string suffix, CancellationToken ct)
    {
        window.Height = 940;
        window.UpdateLayout();
        var scroll = page.FindControl<ScrollViewer>("DeviceScroll")!;
        scroll.Offset = new Vector(0, scroll.Extent.Height);
        window.UpdateLayout();
        // Allow the QR control's built-in fade and theme brush transitions to settle before visual inspection.
        await Task.Delay(450, ct);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using var frame = window.CaptureRenderedFrame();
        frame?.Save(Path.Combine(Path.GetTempPath(), $"andromeld-wireless-{suffix}.png"), PngBitmapEncoderOptions.Default);
        window.Height = 780;
    }

    private static async Task UntilAsync(Func<bool> condition, CancellationToken ct)
    {
        while (!condition()) await Task.Delay(10, ct);
    }

    private sealed class ControlledPairing : IWirelessAdbPairing
    {
        public List<Attempt> Attempts { get; } = [];

        public Task<UsbDevice> PairAsync(AdbPairingCode code, CancellationToken ct)
        {
            Attempt attempt = new(code, ct);
            Attempts.Add(attempt);
            // Deliberately deliver late results to test the UI owner's cancellation and retirement boundary.
            return attempt.Completion.Task;
        }
    }

    private sealed record Attempt(AdbPairingCode Code, CancellationToken Token)
    {
        public TaskCompletionSource<UsbDevice> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
