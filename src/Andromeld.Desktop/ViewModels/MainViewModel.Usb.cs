using System.Collections.ObjectModel;
using System.Net;

using Andromeld.Discovery;

using Avalonia.Threading;

using ReactiveUI.SourceGenerators;

namespace Andromeld.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private readonly IUsbDiscovery _usbDiscovery;
    private readonly CancellationTokenSource _usbDiscoveryLifetime = new();
    private IObservable<bool> _canSubmitWirelessPairing = null!;
    private IUsbConnection? _usbConnection;
    private Task _usbConnectionTask = Task.CompletedTask;
    private Task _usbDiscoveryTask = Task.CompletedTask;
    private bool _usbPreparationActive;
    private TaskCompletionSource? _usbPrivacy;
    private TaskCompletionSource<string>? _wirelessPairing;

    public ObservableCollection<UsbDeviceViewModel> UsbDevices { get; } = [];
    [Reactive]
    public partial string UsbDiscoveryStatus { get; private set; } = "使用 USB 线连接手机，开启并授权 USB 调试";
    [Reactive]
    public partial string ConnectionTransport { get; private set; } = "局域网";
    [Reactive]
    public partial bool IsWaitingForUsbPrivacy { get; private set; }
    [Reactive]
    public partial bool IsUsbProgressIndeterminate { get; private set; } = true;
    [Reactive]
    public partial double UsbProgressPercent { get; private set; }
    [Reactive]
    public partial string UsbConnectionNoticeText { get; private set; } = string.Empty;
    [Reactive]
    public partial bool HasUsbConnectionNotice { get; private set; }
    [Reactive]
    public partial bool IsWaitingForWirelessPairing { get; private set; }
    [Reactive]
    public partial string WirelessPairingCode { get; set; } = "";

    [ReactiveCommand]
    private void ContinueUsbPrivacy()
    {
        if (!IsWaitingForUsbPrivacy || _disposed || _connection.IsCancellationRequested)
        {
            return;
        }

        IsWaitingForUsbPrivacy = false;
        _usbPrivacy?.TrySetResult();
    }

    [ReactiveCommand(CanExecute = nameof(_canSubmitWirelessPairing))]
    private void SubmitWirelessPairing()
    {
        if (_wirelessPairing is not { } completion)
        {
            return;
        }

        string code = (WirelessPairingCode ?? string.Empty).Trim();
        if (!IsValidWirelessPairingCode(code))
        {
            return;
        }

        WirelessPairingCode = string.Empty;
        IsWaitingForWirelessPairing = false;
        completion.TrySetResult(code);
    }

    private void InitializeWireless()
    {
        _canSubmitWirelessPairing = this.WhenAnyValue(x => x.IsWaitingForWirelessPairing, x => x.WirelessPairingCode,
            static (waiting, code) => waiting && code is not null && IsValidWirelessPairingCode(code.Trim()));
    }

    private static bool IsValidWirelessPairingCode(string code)
    {
        return code.Length == 6 && code.All(char.IsAsciiDigit);
    }

    private async Task<IUsbConnection> PrepareAdbConnectionAsync(UsbDevice? device, IPAddress? wirelessAddress = null)
    {
        CancellationTokenSource owner = _connection;
        _usbPreparationActive = true;
        Progress<UsbConnectionProgress> progress = new(value =>
        {
            if (_disposed || _connection != owner || owner.IsCancellationRequested || !_usbPreparationActive)
            {
                return;
            }

            if (value.Notice is { } notice)
            {
                string message = notice switch
                {
                    UsbConnectionNotice.UpdateCheckUnavailable => "官网暂时不可达，暂未检查更新；继续尝试已安装的 helper。",
                    UsbConnectionNotice.RemoteApkCleanupFailed => "手机临时 APK 未能清理，请重新连接后检查 /data/local/tmp/AndroMeld-*.apk。",
                    _ => "电脑临时 APK 未能清理，请检查系统临时目录。"
                };
                UsbConnectionNoticeText = string.IsNullOrEmpty(UsbConnectionNoticeText) ? message : UsbConnectionNoticeText + "\n" + message;
                HasUsbConnectionNotice = true;
                return;
            }

            IsUsbProgressIndeterminate = value.TotalBytes is not > 0;
            UsbProgressPercent = value.TotalBytes is > 0 ? Math.Clamp(100d * value.BytesTransferred / value.TotalBytes.Value, 0, 100) : 0;
            if (value.Stage is UsbConnectionStage.Copying && UsbProgressPercent >= 100)
            {
                Status = "正在安装 helper…";
                IsUsbProgressIndeterminate = true;
            }
            else if (value.TotalBytes is > 0)
            {
                Status = $"{UsbStageStatus(value.Stage)} · {UsbProgressPercent:0}%";
            }
            else if (value.BytesTransferred > 0)
            {
                Status = $"{UsbStageStatus(value.Stage)} · {value.BytesTransferred / 1024:N0} KiB";
            }
            else
            {
                Status = UsbStageStatus(value.Stage) + "…";
            }
        });
        try
        {
            if (wirelessAddress is { } address)
            {
                return await _usbDiscovery.OpenWirelessAsync(address, owner.Token, progress,
                    token => WaitForUsbPrivacyAsync(owner, token), token => WaitForWirelessPairingAsync(owner, token)).ConfigureAwait(true);
            }

            return await _usbDiscovery.OpenAsync(device!, owner.Token, progress, token => WaitForUsbPrivacyAsync(owner, token)).ConfigureAwait(true);
        }
        finally
        {
            _usbPreparationActive = false;
            IsWaitingForUsbPrivacy = false;
            IsWaitingForWirelessPairing = false;
            WirelessPairingCode = string.Empty;
            _wirelessPairing = null;
            IsUsbProgressIndeterminate = true;
        }
    }

    private async Task WaitForUsbPrivacyAsync(CancellationTokenSource owner, CancellationToken ct)
    {
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_disposed || _connection != owner || ct.IsCancellationRequested)
            {
                completion.TrySetCanceled(ct);
                return;
            }

            _usbPrivacy = completion;
            IsWaitingForUsbPrivacy = true;
            Status = "请在手机完成隐私确认";
        });
        try { await completion.Task.WaitAsync(ct).ConfigureAwait(false); }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_usbPrivacy != completion)
                {
                    return;
                }

                _usbPrivacy = null;
                IsWaitingForUsbPrivacy = false;
            });
        }
    }

    private async Task<string> WaitForWirelessPairingAsync(CancellationTokenSource owner, CancellationToken ct)
    {
        TaskCompletionSource<string> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_disposed || _connection != owner || ct.IsCancellationRequested)
            {
                completion.TrySetCanceled(ct);
                return;
            }

            _wirelessPairing = completion;
            WirelessPairingCode = string.Empty;
            IsWaitingForWirelessPairing = true;
            Status = "请在手机上打开无线调试配对，并输入 6 位配对码";
        });
        try { return await completion.Task.WaitAsync(ct).ConfigureAwait(false); }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_wirelessPairing != completion)
                {
                    return;
                }

                _wirelessPairing = null;
                IsWaitingForWirelessPairing = false;
                WirelessPairingCode = string.Empty;
            });
        }
    }

    private static string UsbStageStatus(UsbConnectionStage stage)
    {
        return stage switch
        {
            UsbConnectionStage.CheckingDevice => "正在检查设备",
            UsbConnectionStage.CheckingHelper => "正在检查手机 helper",
            UsbConnectionStage.CheckingUpdates => "正在检查 helper 更新",
            UsbConnectionStage.Downloading => "正在下载 helper",
            UsbConnectionStage.Copying => "正在复制 helper 到手机",
            UsbConnectionStage.Installing => "正在安装 helper",
            UsbConnectionStage.PreparingService => "正在准备手机服务",
            UsbConnectionStage.WaitingForPrivacyConsent => "请在手机完成隐私确认",
            UsbConnectionStage.FindingWirelessService => "正在查找无线调试服务",
            UsbConnectionStage.ConnectingWireless => "正在建立无线调试连接",
            UsbConnectionStage.WaitingForWirelessAuthorization => "请在手机上允许无线调试",
            UsbConnectionStage.WaitingForWirelessPairing => "请在手机上完成无线调试配对",
            UsbConnectionStage.FindingWirelessPairing => "正在查找无线调试配对服务",
            UsbConnectionStage.PairingWireless => "正在配对无线调试",
            _ => "正在建立 ADB 转发"
        };
    }

    private static string UsbStageText(UsbConnectionStage stage)
    {
        return stage switch
        {
            UsbConnectionStage.CheckingDevice => "检查设备",
            UsbConnectionStage.CheckingHelper => "检查手机 helper",
            UsbConnectionStage.CheckingUpdates => "检查 helper 更新",
            UsbConnectionStage.Downloading => "下载 helper",
            UsbConnectionStage.Copying => "复制 helper",
            UsbConnectionStage.Installing => "安装 helper",
            UsbConnectionStage.PreparingService => "准备手机服务",
            UsbConnectionStage.WaitingForPrivacyConsent => "隐私确认",
            UsbConnectionStage.FindingWirelessService => "查找无线调试服务",
            UsbConnectionStage.ConnectingWireless => "建立无线调试连接",
            UsbConnectionStage.WaitingForWirelessAuthorization => "无线调试授权",
            UsbConnectionStage.WaitingForWirelessPairing => "无线调试配对",
            UsbConnectionStage.FindingWirelessPairing => "查找无线调试配对服务",
            UsbConnectionStage.PairingWireless => "无线调试配对",
            _ => "建立 ADB 转发"
        };
    }

    [ReactiveCommand(CanExecute = nameof(_canRefreshDiscovery))]
    private Task RefreshUsbDevicesAsync()
    {
        if (_disposed)
        {
            return Task.CompletedTask;
        }

        if (_usbDiscoveryTask.IsCompleted)
        {
            _usbDiscoveryTask = FindUsbDevicesAsync();
        }

        return _usbDiscoveryTask;
    }

    private async Task FindUsbDevicesAsync()
    {
        CancellationToken ct = _usbDiscoveryLifetime.Token;
        UsbDiscoveryStatus = "正在查找 USB 设备…";
        try
        {
            IReadOnlyList<UsbDevice> snapshot = await _usbDiscovery.FindAsync(ct).ConfigureAwait(true);
            ct.ThrowIfCancellationRequested();
            foreach (UsbDeviceViewModel item in UsbDevices
                         .Where(item => !snapshot.Any(device => string.Equals(device.Serial, item.Serial, StringComparison.Ordinal))).ToArray())
            {
                UsbDevices.Remove(item);
            }

            foreach (UsbDevice device in snapshot)
            {
                UsbDeviceViewModel? item = UsbDevices.FirstOrDefault(item => string.Equals(item.Serial, device.Serial, StringComparison.Ordinal));
                if (item is null)
                {
                    UsbDevices.Add(new UsbDeviceViewModel(device, ConnectUsbCommand, RePairUsbCommand));
                }
                else
                {
                    item.Update(device);
                }
            }

            UsbDiscoveryStatus = UsbDevices.Count == 0 ? "未发现 USB 设备，请开启 USB 调试并重新查找" : $"发现 {UsbDevices.Count} 台 USB 设备";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            /* Discovery stops with its UI owner. */
        }
        catch (Exception ex)
        {
            UsbDevices.Clear();
            UsbDiscoveryStatus = $"USB 设备查找失败：{ex.Message}";
        }
    }

    [ReactiveCommand(CanExecute = nameof(_canRefreshDiscovery))]
    private Task ConnectUsbAsync(UsbDeviceViewModel device)
    {
        return _usbConnectionTask = ConnectUsbCoreAsync(false, device);
    }

    [ReactiveCommand(CanExecute = nameof(_canRefreshDiscovery))]
    private Task RePairUsbAsync(UsbDeviceViewModel device)
    {
        return _usbConnectionTask = ConnectUsbCoreAsync(true, device);
    }

    private async Task ConnectUsbCoreAsync(bool forcePair, UsbDeviceViewModel device)
    {
        try { await ConnectCoreAsync(forcePair, usb: device).ConfigureAwait(true); }
        catch (OperationCanceledException) when (_connection.IsCancellationRequested)
        {
            // Cancel and shutdown are normal command completion. No delayed error may outlive the UI owner.
        }
    }

    private async Task ReleaseUsbConnectionAsync()
    {
        IUsbConnection? connection = _usbConnection;
        _usbConnection = null;
        if (connection is null)
        {
            return;
        }

        try { await connection.DisposeAsync().ConfigureAwait(true); }
        catch (Exception ex) { Error = $"ADB 转发清理失败：{ex.Message}"; }
    }

    private async Task DisposeUsbDiscoveryAsync()
    {
        await _usbDiscoveryLifetime.CancelAsync().ConfigureAwait(true);
        await _connection.CancelAsync().ConfigureAwait(true);
        await _usbDiscoveryTask.ConfigureAwait(true);
        try { await _usbConnectionTask.ConfigureAwait(true); }
        catch (Exception ex) { Report(ex); }

        _usbDiscoveryLifetime.Dispose();
    }
}
