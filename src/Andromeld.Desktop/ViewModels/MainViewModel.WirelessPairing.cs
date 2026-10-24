using Andromeld.Discovery;
using Andromeld.Protocol;

using ReactiveUI;
using ReactiveUI.Primitives;
using ReactiveUI.Primitives.Disposables;
using ReactiveUI.SourceGenerators;

namespace Andromeld.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private readonly IWirelessAdbPairing _wirelessAdbPairing;
    private CancellationTokenSource? _qrPairing;
    private Task _qrPairingTask = Task.CompletedTask;
    private int _qrRevision;
    private bool _openingWirelessDevice;

    [Reactive(nameof(IsQrConnection), nameof(IsIpConnection))]
    public partial ConnectionMethod SelectedConnectionMethod { get; private set; }
    public bool IsQrConnection => SelectedConnectionMethod == ConnectionMethod.QrCode;
    public bool IsIpConnection => SelectedConnectionMethod == ConnectionMethod.IpAddress;
    [Reactive(nameof(HasQrCode))]
    public partial string? PairingQrData { get; private set; }
    public bool HasQrCode => !string.IsNullOrEmpty(PairingQrData);
    [Reactive]
    public partial string QrPairingStatus { get; private set; } = "等待扫码";

    private void InitializeWirelessPairing()
    {
        this.WhenAnyValue(model => model.IsQrConnection, model => model.IsDevicePage, model => model.CanSelectNearbyDevice)
            .Subscribe(_ => UpdateQrPairing()).DisposeWith(_commands);
    }

    [ReactiveCommand]
    private void ShowQrConnection() => SelectedConnectionMethod = ConnectionMethod.QrCode;

    [ReactiveCommand]
    private void ShowIpConnection() => SelectedConnectionMethod = ConnectionMethod.IpAddress;

    [ReactiveCommand]
    private void RegeneratePairingQr() => UpdateQrPairing();

    private void UpdateQrPairing()
    {
        // UI owns selection, cancellation and all properties. A replacement waits for the old attempt to finish.
        if (_disposed)
        {
            return;
        }

        if (_openingWirelessDevice && IsQrConnection && IsDevicePage)
        {
            return;
        }

        _qrPairing?.Cancel();
        PairingQrData = null;
        int revision = ++_qrRevision;
        _qrPairingTask = RestartQrPairingAsync(_qrPairingTask, revision);
    }

    private async Task RestartQrPairingAsync(Task previous, int revision)
    {
        await previous.ConfigureAwait(true);
        if (_disposed || revision != _qrRevision || !IsQrConnection || !IsDevicePage || !CanSelectNearbyDevice)
        {
            return;
        }

        using CancellationTokenSource attempt = new();
        _qrPairing = attempt;
        AdbPairingCode code = AdbPairingCode.Create();
        PairingQrData = code.QrData;
        QrPairingStatus = "等待扫码";
        try
        {
            UsbDevice device = await _wirelessAdbPairing.PairAsync(code, attempt.Token).ConfigureAwait(true);
            attempt.Token.ThrowIfCancellationRequested();
            if (_disposed || revision != _qrRevision || !CanSelectNearbyDevice)
            {
                return;
            }

            PairingQrData = null;
            QrPairingStatus = "已配对，正在连接设备…";
            _openingWirelessDevice = true;
            try
            {
                UsbDeviceViewModel selected = new(device, ConnectUsbCommand, RePairUsbCommand);
                await ConnectCoreAsync(false, usb: selected, ct: attempt.Token).ConfigureAwait(true);
                if (!_disposed && revision == _qrRevision)
                {
                    QrPairingStatus = IsConnected ? "设备已连接" : "连接已取消，请重新生成二维码后重试";
                }
            }
            finally { _openingWirelessDevice = false; }
        }
        catch (OperationCanceledException) when (attempt.IsCancellationRequested)
        {
            // Leaving the page, another connection or regeneration retires this attempt without a late error.
        }
        catch (Exception ex)
        {
            if (!_disposed && revision == _qrRevision)
            {
                PairingQrData = null;
                QrPairingStatus = ex is OperationCanceledException ? "无线调试连接超时，请重新生成二维码后重试" : ex.Message;
            }
        }
        finally
        {
            if (ReferenceEquals(_qrPairing, attempt))
            {
                _qrPairing = null;
            }
        }
    }

    private async Task DisposeWirelessPairingAsync()
    {
        _qrRevision++;
        if (_qrPairing is { } attempt)
        {
            await attempt.CancelAsync().ConfigureAwait(true);
        }
        await _qrPairingTask.ConfigureAwait(true);
        PairingQrData = null;
    }
}
