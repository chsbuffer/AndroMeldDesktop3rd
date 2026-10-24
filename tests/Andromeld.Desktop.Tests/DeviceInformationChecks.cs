using Andromeld.Desktop.ViewModels;
using Andromeld.Desktop.Views;
using Andromeld.Protocol;
using Andromeld.Testing;
using Avalonia.Controls;
using ReactiveUI.Primitives;

namespace Andromeld.Desktop.Tests;

internal static class DeviceInformationChecks
{
    public static async Task RunAsync(MainViewModel model, MainWindow main, MockPhone phone, CancellationToken ct)
    {
        var page = main.FindControl<DeviceConnectionView>("DevicePage")!;
        Program.Check(phone.DeviceInformationRequests == 1 && model.DeviceIdentifier == "mock" && model.DeviceBatteryText == "87%" &&
            model.HasDeviceStorage && Math.Abs(model.DeviceStoragePercent - 93.63557105) < .001 &&
            model.DeviceStorageText.Contains("107.4") && model.DeviceStorageText.Contains("114.7"),
            "connected device card loads actual battery and decimal GB storage usage once after authentication");
        phone.DeviceInformationOverride = new DeviceInformation("mock", "模拟手机", "16", 36, new(42, true), new(256_000_000_000, 128_000_000_000));
        await model.ShowApplicationsCommand.Execute().ToTask();
        await model.ShowDeviceCommand.Execute().ToTask();
        main.UpdateLayout();
        Program.Check(phone.DeviceInformationRequests == 2 && model.IsDeviceCharging && model.DeviceBatteryText == "42%" &&
            model.DeviceStoragePercent == 50 && page.FindControl<ProgressBar>("DeviceStorageBar")!.Value == 50,
            "returning to Devices refreshes charging status and storage usage");

        phone.DeviceInformationOverride = new { battery = new { level = int.MinValue, charging = false } };
        await model.ShowDeviceCommand.Execute().ToTask(); main.UpdateLayout();
        Program.Check(!model.HasDeviceBattery && model.DeviceBatteryText == "电量未知" && !model.HasDeviceStorage &&
            !page.FindControl<StackPanel>("DeviceStorage")!.IsEffectivelyVisible && model.DeviceIdentifier == "mock",
            "unavailable battery/storage replace old values without showing fabricated zeroes or losing the authenticated identity");

        foreach (var (total, free) in new[] { (0L, 0L), (100L, -1L), (100L, 101L) })
        {
            phone.DeviceInformationOverride = new { storage = new { total, free } };
            await model.ShowDeviceCommand.Execute().ToTask();
            Program.Check(!model.HasDeviceStorage && model.DeviceStoragePercent == 0,
                $"invalid storage counts do not reach the usage bar ({total}/{free})");
        }
        phone.DeviceInformationError = "unavailable";
        await model.ShowDeviceCommand.Execute().ToTask(); main.UpdateLayout();
        Program.Check(model.IsConnected && model.CanOpenTerminal && !model.HasDeviceStorage &&
            page.FindControl<TextBlock>("DeviceInformationStatus")!.IsEffectivelyVisible && model.DeviceInformationStatus.Contains("暂不可用"),
            "device information failures leave connected actions usable and show an inline retry hint");

        phone.DeviceInformationError = null;
        phone.DeviceInformationOverride = null;
        phone.DeviceInformationReplyGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var before = phone.DeviceInformationRequests;
        var load = model.ShowDeviceCommand.Execute().ToTask();
        while (phone.DeviceInformationRequests == before) await Task.Delay(10, ct);
        await model.DisconnectCommand.Execute().ToTask();
        phone.DeviceInformationReplyGate.SetResult();
        await load;
        Program.Check(model.ConnectedDeviceInformation is null && model.DeviceIdentifier == "" && !model.IsConnected,
            "disconnect clears device details and a delayed old reply cannot repopulate the card");
        phone.DeviceInformationReplyGate = null;
        await model.ConnectCommand.Execute().ToTask();
        Program.Check(model.DeviceIdentifier == "mock" && model.DeviceBatteryText == "87%" && model.HasDeviceStorage,
            "reconnecting obtains fresh device details after cancellation");
    }
}
