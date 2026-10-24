using System.Collections.ObjectModel;

using Andromeld.Discovery;

using ReactiveUI.SourceGenerators;

namespace Andromeld.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private readonly ILanDiscovery _discovery;
    private IObservable<bool> _canRefreshDiscovery = null!;
    private bool _discoveryStarted, _discoveryDisposed;
    private Task _nearbyConnectionTask = Task.CompletedTask;

    public ObservableCollection<NearbyDeviceViewModel> NearbyDevices { get; } = [];

    [Reactive]
    public partial bool IsResolvingDevice { get; private set; }
    [Reactive]
    public partial string DiscoveryStatus { get; private set; } = "正在查找附近设备…";
    public bool CanSelectNearbyDevice => !IsConnected && !IsConnecting && !IsDisconnecting;

    private void InitializeDiscovery()
    {
        _canRefreshDiscovery = this.WhenAnyValue(x => x.CanSelectNearbyDevice);
        _discovery.DevicesChanged += DiscoveryChanged;
    }

    [ReactiveCommand(CanExecute = nameof(_canRefreshDiscovery))]
    private Task ConnectNearbyAsync(NearbyDeviceViewModel device)
    {
        return _nearbyConnectionTask = ConnectCoreAsync(false, device);
    }

    [ReactiveCommand(CanExecute = nameof(_canRefreshDiscovery))]
    private Task RePairNearbyAsync(NearbyDeviceViewModel device)
    {
        return _nearbyConnectionTask = ConnectCoreAsync(true, device);
    }

    public void StartDiscovery()
    {
        if (_discoveryStarted || _discoveryDisposed)
        {
            return;
        }

        _ = RefreshUsbDevicesAsync();
        try
        {
            _discovery.Start();
            _discoveryStarted = true;
            ApplyDiscoveredDevices();
        }
        catch (Exception ex) { DiscoveryStatus = $"设备发现失败：{ex.Message}"; }
    }

    [ReactiveCommand(CanExecute = nameof(_canRefreshDiscovery))]
    private void RefreshDiscovery()
    {
        try
        {
            _discovery.Refresh();
            _discoveryStarted = true;
            ApplyDiscoveredDevices();
        }
        catch (Exception ex) { DiscoveryStatus = $"设备发现失败：{ex.Message}"; }
    }

    private void DiscoveryChanged(object? sender, EventArgs e)
    {
        if (!_discoveryDisposed)
        {
            ApplyDiscoveredDevices();
        }
    }

    private void ApplyDiscoveredDevices()
    {
        IReadOnlyList<LanDevice> snapshot = _discovery.Devices;
        foreach (NearbyDeviceViewModel item in NearbyDevices
                     .Where(item => !snapshot.Any(d => string.Equals(d.Key, item.Device.Key, StringComparison.Ordinal))).ToArray())
        {
            NearbyDevices.Remove(item);
        }

        foreach (LanDevice device in snapshot)
        {
            NearbyDeviceViewModel? item = NearbyDevices.FirstOrDefault(item => string.Equals(item.Device.Key, device.Key, StringComparison.Ordinal));
            if (item is null)
            {
                NearbyDevices.Add(new NearbyDeviceViewModel(device, ConnectNearbyCommand, RePairNearbyCommand));
            }
            else
            {
                item.Update(device);
            }
        }

        DiscoveryStatus = NearbyDevices.Count == 0
            ? "暂未发现设备，请开启手机的局域网发现"
            : $"发现 {NearbyDevices.Count} 台设备，点击卡片连接";
    }

    private async Task DisposeDiscoveryAsync()
    {
        _discoveryDisposed = true;
        _discovery.DevicesChanged -= DiscoveryChanged;
        _discovery.Dispose();
        await _connection.CancelAsync().ConfigureAwait(true);
        try { await _nearbyConnectionTask.ConfigureAwait(true); }
        catch (Exception)
        {
            /* The generated command reports its own failure; shutdown still releases the connection. */
        }
    }
}