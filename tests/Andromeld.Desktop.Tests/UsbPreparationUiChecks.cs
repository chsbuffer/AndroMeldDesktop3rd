using Andromeld.Desktop.Services;
using Andromeld.Desktop.ViewModels;
using Andromeld.Desktop.Views;
using Andromeld.Discovery;
using Andromeld.Protocol;
using Andromeld.Testing;
using Avalonia.Controls;
using Avalonia.Headless;
using ReactiveUI.Primitives;

namespace Andromeld.Desktop.Tests;

internal static class UsbPreparationUiChecks
{
    private const string PrivacyRequired = "Result: Bundle[{status=error, errorCode=privacy_consent_required}]";

    public static async Task RunAsync(string temporary, CancellationToken ct)
    {
        await using var phone = await MockPhone.StartAsync("usb-preparation-synthetic");
        await using var adb = new FakeAdbServer(phone.Endpoint.Port) { HelperInstalled = false, BootstrapOutput = PrivacyRequired };
        var downloads = new FakeUsbDownloads();
        using var http = new HttpClient(downloads);
        await using var model = new MainViewModel(new Settings { StoragePath = Path.Combine(temporary, "usb-preparation", "settings.json"), AudioForwardingEnabled = false, ClipboardSyncEnabled = false },
            new StubDiscovery(), usbDiscovery: new UsbDiscovery(adb.Endpoint, httpClient: http));
        var window = new MainWindow { DataContext = model };
        window.Show();
        try
        {
            await model.RefreshUsbDevicesCommand.Execute().ToTask();
            var installStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var installReply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            adb.BeforeShellReply = command =>
            {
                if (!command.StartsWith("shell:pm install", StringComparison.Ordinal)) return Task.CompletedTask;
                installStarted.TrySetResult();
                return installReply.Task;
            };
            var sawDownloadBytes = false;
            var sawCopyBytes = false;
            model.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName != nameof(model.UsbProgressPercent) || model.UsbProgressPercent <= 0) return;
                if (model.Status.Contains("下载")) sawDownloadBytes = true;
                if (model.Status.Contains("复制")) sawCopyBytes = true;
            };
            var opening = model.ConnectUsbCommand.Execute(model.UsbDevices.Single()).ToTask();
            await installStarted.Task.WaitAsync(ct);
            await UntilAsync(() => model.Status.Contains("正在安装"), ct);
            window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            var view = window.FindControl<DeviceConnectionView>("DevicePage")!;
            Program.Check(sawDownloadBytes && sawCopyBytes && view.FindControl<ProgressBar>("ConnectionProgressBar")!.IsIndeterminate &&
                model.IsConnecting && !model.CanConnect && adb.ForwardCount == 0, "USB preparation renders actual download/copy progress then waits for installation before enabling business connections");
            installReply.SetResult();
            await UntilAsync(() => model.IsWaitingForUsbPrivacy, ct);
            window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            var continueButton = view.FindControl<Button>("UsbPrivacyContinueButton")!;
            Program.Check(continueButton.IsEffectivelyVisible && model.Status.Contains("隐私") && adb.ForwardCount == 0 && !opening.IsCompleted,
                "privacy confirmation displays a continue action while connection initialization remains pending");
            continueButton.Command!.Execute(null);
            await UntilAsync(() => model.IsWaitingForUsbPrivacy && adb.Requests.Count(r => r == "shell:" + UsbBootstrap.ShellCommand) >= 2, ct);
            Program.Check(adb.ForwardCount == 0 && !opening.IsCompleted, "continuing without phone consent returns to privacy waiting without accepting it automatically");

            adb.BootstrapOutput = "Result: Bundle[{status=error, errorCode=web_service_unavailable}]";
            await model.ContinueUsbPrivacyCommand.Execute().ToTask();
            try { await opening; throw new Exception("Expected continuation failure"); }
            catch (UsbConnectionException) { }
            await UntilAsync(() => model.HasError, ct);
            Program.Check(model.Error.Contains("手机服务") && !model.IsConnecting && !model.IsWaitingForUsbPrivacy && model.CanSelectNearbyDevice &&
                adb.ForwardCount == 0,
                "errors after privacy continuation enter the ordinary UI error path and restore retry actions");
            var deviceChecks = adb.Requests.Count(r => r == "host:devices-l");
            adb.BootstrapOutput = "Result: Bundle[{status=ok, responseVersion=2, webPort=13019, ticket=39U6DY5U}]";
            await model.ConnectUsbCommand.Execute(model.UsbDevices.Single()).ToTask();
            Program.Check(model.IsConnected && adb.Requests.Count(r => r == "host:devices-l") > deviceChecks && adb.HelperVersion == 100,
                "manual retry rechecks the device and reuses the installation completed during the previous attempt");
            await model.DisconnectCommand.Execute().ToTask();

            downloads.Unavailable = true;
            await model.ConnectUsbCommand.Execute(model.UsbDevices.Single()).ToTask();
            Program.Check(model.IsConnected && model.HasUsbConnectionNotice && model.UsbConnectionNoticeText.Contains("暂未检查更新"),
                "offline update-check notice survives a successful connection with the existing helper");
            await model.DisconnectCommand.Execute().ToTask();
            downloads.Unavailable = false;
            adb.BootstrapOutput = PrivacyRequired;
            opening = model.ConnectUsbCommand.Execute(model.UsbDevices.Single()).ToTask();
            await UntilAsync(() => model.IsWaitingForUsbPrivacy, ct);
            await model.CancelConnectCommand.Execute().ToTask();
            await opening;
            Program.Check(!model.IsWaitingForUsbPrivacy && !model.IsConnecting && model.CanSelectNearbyDevice && adb.ForwardCount == 0,
                "canceling privacy waiting exits the pending operation and restores connection actions");
        }
        finally
        {
            window.DataContext = null; window.Close();
        }

        foreach (var stage in new[] { UsbConnectionStage.CheckingHelper, UsbConnectionStage.CheckingUpdates, UsbConnectionStage.Downloading,
            UsbConnectionStage.Copying, UsbConnectionStage.Installing, UsbConnectionStage.PreparingService })
        {
            var source = new GatedDiscovery(stage, phone.Endpoint);
            await using var pendingModel = new MainViewModel(new Settings { StoragePath = Path.Combine(temporary, "usb-cancel-" + stage, "settings.json") },
                new StubDiscovery(), usbDiscovery: source);
            await pendingModel.RefreshUsbDevicesCommand.Execute().ToTask();
            var opening = pendingModel.ConnectUsbCommand.Execute(pendingModel.UsbDevices.Single()).ToTask();
            await source.Entered.Task.WaitAsync(ct);
            await pendingModel.CancelConnectCommand.Execute().ToTask();
            source.Progress?.Report(new(UsbConnectionStage.Downloading, 999, 1000));
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
            Program.Check(pendingModel.IsConnecting && !pendingModel.CanConnect && pendingModel.Status.Contains("取消"),
                $"cancel at {stage} waits for in-flight work and ignores late progress");
            source.Release.SetResult();
            await opening;
            Program.Check(!pendingModel.IsConnecting && !pendingModel.IsConnected && pendingModel.CanSelectNearbyDevice && source.Connection.Disposed,
                $"cancel at {stage} disposes a late lease before authentication or enabling another connection");
        }

        await using var closingAdb = new FakeAdbServer(phone.Endpoint.Port) { BootstrapOutput = PrivacyRequired };
        await using var closingModel = new MainViewModel(new Settings { StoragePath = Path.Combine(temporary, "usb-privacy-close", "settings.json") },
            new StubDiscovery(), usbDiscovery: new UsbDiscovery(closingAdb.Endpoint, httpClient: http));
        var closingWindow = new MainWindow { DataContext = closingModel };
        closingWindow.Show();
        await closingModel.RefreshUsbDevicesCommand.Execute().ToTask();
        var connecting = closingModel.ConnectUsbCommand.Execute(closingModel.UsbDevices.Single()).ToTask();
        await UntilAsync(() => closingModel.IsWaitingForUsbPrivacy, ct);
        closingWindow.Close();
        await connecting;
        await closingModel.DisposeAsync();
        Program.Check(!closingModel.IsWaitingForUsbPrivacy && !closingModel.IsConnected && closingAdb.ForwardCount == 0,
            "closing the main window cancels privacy waiting and leaves no USB forward");
    }

    private static async Task UntilAsync(Func<bool> condition, CancellationToken ct)
    {
        while (!condition()) await Task.Delay(10, ct);
    }

    /// <summary>Models a boundary operation that finishes after cancellation; no phone or network is involved.</summary>
    private sealed class GatedDiscovery(UsbConnectionStage stage, Uri endpoint) : IUsbDiscovery
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IProgress<UsbConnectionProgress>? Progress { get; private set; }
        public LateConnection Connection { get; } = new(endpoint);
        public Task<IReadOnlyList<UsbDevice>> FindAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<UsbDevice>>([new("USB-UI", "Synthetic", UsbDeviceState.Online)]);
        public async Task<IUsbConnection> OpenAsync(UsbDevice device, CancellationToken ct, IProgress<UsbConnectionProgress>? progress = null, Func<CancellationToken, Task>? confirmPrivacy = null)
        {
            Progress = progress;
            progress?.Report(new(stage));
            Entered.SetResult();
            await Release.Task;
            // Some boundary operations finish despite cancellation. The UI must reject and dispose this lease.
            return Connection;
        }

        public Task<IUsbConnection> OpenWirelessAsync(System.Net.IPAddress address, CancellationToken ct, IProgress<UsbConnectionProgress>? progress = null,
            Func<CancellationToken, Task>? confirmPrivacy = null, Func<CancellationToken, Task<string>>? requestPairingCode = null) =>
            throw new InvalidOperationException("No synthetic wireless device is available.");
    }

    private sealed class LateConnection(Uri endpoint) : IUsbConnection
    {
        public Uri Endpoint { get; } = endpoint;
        public UsbBootstrap Bootstrap { get; } = new(13019, "39U6DY5U");
        public bool Disposed { get; private set; }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
