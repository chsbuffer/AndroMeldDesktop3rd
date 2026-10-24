using System.Globalization;

using Andromeld.Protocol;

using Lucide.Avalonia;

using ReactiveUI.SourceGenerators;

namespace Andromeld.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    // UI-owned. At most one request per connection; an old connection cannot update a new card.
    private AndromeldClient? _deviceInformationClient;
    private Task? _deviceInformationLoad;

    [Reactive]
    public partial string DeviceIdentifier { get; private set; } = "";

    [Reactive(nameof(HasDeviceBattery), nameof(DeviceBatteryText), nameof(DeviceBatteryDescription),
        nameof(DeviceBatteryIcon), nameof(IsDeviceCharging), nameof(HasDeviceStorage),
        nameof(DeviceStorageText), nameof(DeviceStoragePercent), nameof(DeviceStoragePercentText))]
    public partial DeviceInformation? ConnectedDeviceInformation { get; private set; }

    [Reactive]
    public partial string DeviceInformationStatus { get; private set; } = "正在读取设备信息…";

    public bool HasDeviceBattery => ConnectedDeviceInformation?.Battery?.Level is >= 0 and <= 100;
    public bool IsDeviceCharging => HasDeviceBattery && ConnectedDeviceInformation?.Battery?.Charging == true;
    public string DeviceBatteryText => HasDeviceBattery ? $"{ConnectedDeviceInformation!.Battery!.Level}%" : "电量未知";
    public string DeviceBatteryDescription
    {
        get
        {
            if (!HasDeviceBattery)
            {
                return "设备未提供有效电量";
            }

            string charging = IsDeviceCharging ? " · 正在充电" : "";
            return $"电量 {DeviceBatteryText}{charging}";
        }
    }
    public LucideIconKind DeviceBatteryIcon => ConnectedDeviceInformation?.Battery?.Level switch
    {
        >= 90 and <= 100 => LucideIconKind.BatteryFull,
        >= 50 and < 90 => LucideIconKind.BatteryMedium,
        > 0 and < 50 => LucideIconKind.BatteryLow,
        _ => LucideIconKind.Battery
    };

    public bool HasDeviceStorage => ConnectedDeviceInformation?.Storage is { Total: > 0, Free: >= 0 } storage && storage.Free <= storage.Total;
    public double DeviceStoragePercent => HasDeviceStorage
        ? (ConnectedDeviceInformation!.Storage!.Total!.Value - ConnectedDeviceInformation.Storage.Free!.Value) * 100d /
          ConnectedDeviceInformation.Storage.Total.Value
        : 0;
    public string DeviceStoragePercentText => $"{DeviceStoragePercent:0}%";
    public string DeviceStorageText => HasDeviceStorage
        ? $"已用 {Gigabytes(ConnectedDeviceInformation!.Storage!.Total!.Value - ConnectedDeviceInformation.Storage.Free!.Value)} / {Gigabytes(ConnectedDeviceInformation.Storage.Total.Value)}"
        : "";

    private static string Gigabytes(long bytes)
    {
        return $"{(bytes / 1e9).ToString("0.0", CultureInfo.CurrentCulture)} GB";
    }

    private Task RefreshDeviceInformationAsync()
    {
        if (!IsConnected || _client is not { } client)
        {
            return Task.CompletedTask;
        }

        if (_deviceInformationClient == client && _deviceInformationLoad is { IsCompleted: false })
        {
            return _deviceInformationLoad;
        }

        _deviceInformationClient = client;
        return _deviceInformationLoad = LoadDeviceInformationAsync(client, _connection.Token);
    }

    private async Task LoadDeviceInformationAsync(AndromeldClient client, CancellationToken ct)
    {
        try
        {
            DeviceInformation information = await client.GetDeviceInformationAsync(ct).ConfigureAwait(true);
            if (_client != client || !IsConnected || ct.IsCancellationRequested)
            {
                return;
            }

            ConnectedDeviceInformation = information;
            DeviceInformationStatus = HasDeviceStorage ? "" : "存储信息不可用";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            /* Disconnect owns cancellation. */
        }
        catch (Exception)
        {
            if (_client != client || !IsConnected || ct.IsCancellationRequested)
            {
                return;
            }

            ConnectedDeviceInformation = null;
            DeviceInformationStatus = "设备信息暂不可用，返回设备页时重试";
        }
    }

    private void ClearDeviceInformation()
    {
        DeviceIdentifier = "";
        _deviceInformationClient = null;
        _deviceInformationLoad = null;
        ConnectedDeviceInformation = null;
        DeviceInformationStatus = "正在读取设备信息…";
    }
}