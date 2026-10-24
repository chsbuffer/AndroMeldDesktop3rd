using System.Net;

using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.Models;

using Andromeld.Protocol;

namespace Andromeld.Discovery;

public sealed class UsbDiscovery : IUsbDiscovery
{
    private static readonly HttpClient DefaultHttp = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly Func<CancellationToken, Task>? _ensureServer;
    private readonly HttpClient _http;
    private readonly EndPoint _serverEndpoint;

    public UsbDiscovery(Func<CancellationToken, Task>? ensureServer = null, HttpClient? httpClient = null) : this(
        new IPEndPoint(IPAddress.Loopback, 5037), ensureServer, httpClient)
    {
    }

    public UsbDiscovery(EndPoint serverEndpoint, Func<CancellationToken, Task>? ensureServer = null, HttpClient? httpClient = null)
    {
        _serverEndpoint = serverEndpoint;
        _ensureServer = ensureServer;
        _http = httpClient ?? DefaultHttp;
    }

    public async Task<IReadOnlyList<UsbDevice>> FindAsync(CancellationToken ct)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        if (_ensureServer is not null)
        {
            await _ensureServer(timeout.Token).ConfigureAwait(false);
        }

        IEnumerable<DeviceData> devices = await new AdbClient(_serverEndpoint).GetDevicesAsync(timeout.Token).ConfigureAwait(false);
        return devices.Where(IsUsbDevice).Select(static device => new UsbDevice(device.Serial,
            string.IsNullOrWhiteSpace(device.Model) ? device.Serial : device.Model.Replace('_', ' '), MapState(device.State))).ToArray();
    }

    public async Task<IUsbConnection> OpenAsync(UsbDevice device, CancellationToken ct, IProgress<UsbConnectionProgress>? progress = null,
        Func<CancellationToken, Task>? confirmPrivacy = null)
    {
        AdbClient client = new(_serverEndpoint);
        UsbPreparation preparation = new(client, _http, progress, confirmPrivacy);
        try
        {
            await EnsureServerAsync(ct).ConfigureAwait(false);
            return await OpenPreparedAsync(client, preparation, device.Serial, device.IsWireless, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException ex)
        {
            throw new UsbConnectionException(preparation.Stage, new IOException("ADB 转发分配超时，请检查连接后重试。", ex));
        }
        catch (Exception ex) { throw new UsbConnectionException(preparation.Stage, ex); }
    }

    public async Task<IUsbConnection> OpenWirelessAsync(IPAddress address, CancellationToken ct, IProgress<UsbConnectionProgress>? progress = null,
        Func<CancellationToken, Task>? confirmPrivacy = null, Func<CancellationToken, Task<string>>? requestPairingCode = null)
    {
        AdbClient client = new(_serverEndpoint);
        UsbPreparation preparation = new(client, _http, progress, confirmPrivacy);
        try
        {
            preparation.Report(UsbConnectionStage.FindingWirelessService);
            await EnsureServerAsync(ct).ConfigureAwait(false);
            WirelessAdbConnector connector = new(client, _serverEndpoint, preparation);
            string serial = await connector.ConnectAsync(address, requestPairingCode, ct).ConfigureAwait(false);
            return await OpenPreparedAsync(client, preparation, serial, true, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException ex)
        {
            throw new UsbConnectionException(preparation.Stage, new IOException("无线调试连接超时，请检查网络和手机设置后重试。", ex));
        }
        catch (Exception ex) { throw new UsbConnectionException(preparation.Stage, ex); }
    }

    internal static bool IsUsbDevice(DeviceData device)
    {
        return !string.IsNullOrWhiteSpace(device.Serial) &&
               (!string.IsNullOrWhiteSpace(device.Usb) ||
                (!device.Serial.Contains(':') && !device.Serial.StartsWith("emulator-", StringComparison.Ordinal) &&
                 !device.Serial.Contains("._adb", StringComparison.OrdinalIgnoreCase)));
    }

    private static async Task<IUsbConnection> OpenPreparedAsync(AdbClient client, UsbPreparation preparation, string serial, bool wireless,
        CancellationToken ct)
    {
        (DeviceData selected, UsbBootstrap bootstrap) = await preparation.PrepareAsync(serial, wireless, ct).ConfigureAwait(false);
        preparation.Report(UsbConnectionStage.Forwarding);
        ct.ThrowIfCancellationRequested();
        // Once ADB starts allocating tcp:0, read the assigned port even if the caller cancels,
        // so cancellation can remove precisely this forward rather than losing its handle.
        using CancellationTokenSource allocation = new(TimeSpan.FromSeconds(5));
        int port = await client.CreateForwardAsync(selected, "tcp:0", $"tcp:{bootstrap.WebPort}", false, allocation.Token).ConfigureAwait(false);
        if (port is < 1 or > 65535)
        {
            throw new IOException("ADB 未返回有效的转发端口。");
        }

        UsbConnection connection = new(client, selected, port, bootstrap);
        try
        {
            ct.ThrowIfCancellationRequested();
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private Task EnsureServerAsync(CancellationToken ct)
    {
        return _ensureServer is null
            ? Task.CompletedTask
            : UsbPreparation.WithTimeoutAsync(async token =>
            {
                await _ensureServer(token).ConfigureAwait(false);
                return true;
            }, 15, ct);
    }

    private static UsbDeviceState MapState(DeviceState state)
    {
        return state switch
        {
            DeviceState.Online => UsbDeviceState.Online,
            DeviceState.Unauthorized or DeviceState.Authorizing => UsbDeviceState.Unauthorized,
            _ => UsbDeviceState.Unavailable
        };
    }

    private sealed class UsbConnection(AdbClient client, DeviceData device, int port, UsbBootstrap bootstrap) : IUsbConnection
    {
        private bool _disposed;
        public Uri Endpoint { get; } = new UriBuilder("ws", IPAddress.Loopback.ToString(), port, AndromeldEndpoint.ControlPath).Uri;
        public UsbBootstrap Bootstrap { get; } = bootstrap;

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(3));
            await client.RemoveForwardAsync(device, port, timeout.Token).ConfigureAwait(false);
        }
    }
}
