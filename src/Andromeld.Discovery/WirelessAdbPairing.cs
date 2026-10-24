using System.Net;

using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.Models;

using Andromeld.Protocol;

namespace Andromeld.Discovery;

/// <summary>Each attempt owns its ADB clients and polling loop; cancellation waits for every socket to close.</summary>
public sealed class WirelessAdbPairing : IWirelessAdbPairing
{
    private readonly EndPoint _serverEndpoint;
    private readonly Func<CancellationToken, Task>? _ensureServer;

    public WirelessAdbPairing(Func<CancellationToken, Task>? ensureServer = null)
        : this(new IPEndPoint(IPAddress.Loopback, 5037), ensureServer) { }

    public WirelessAdbPairing(EndPoint serverEndpoint, Func<CancellationToken, Task>? ensureServer = null)
    {
        _serverEndpoint = serverEndpoint;
        _ensureServer = ensureServer;
    }

    public async Task<UsbDevice> PairAsync(AdbPairingCode code, CancellationToken ct)
    {
        if (_ensureServer is not null)
        {
            using CancellationTokenSource startup = CancellationTokenSource.CreateLinkedTokenSource(ct);
            startup.CancelAfter(TimeSpan.FromSeconds(15));
            await _ensureServer(startup.Token).ConfigureAwait(false);
        }

        AdbMdnsService? pairing;
        do
        {
            pairing = (await FindServicesAsync(ct).ConfigureAwait(false)).FirstOrDefault(service =>
                string.Equals(service.Type, AdbMdns.PairingService, StringComparison.Ordinal) && string.Equals(service.Instance, code.ServiceName, StringComparison.Ordinal));
            if (pairing is null)
            {
                await Task.Delay(750, ct).ConfigureAwait(false);
            }
        } while (pairing is null);

        AdbClient client = new(_serverEndpoint);
        using (CancellationTokenSource pairingTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            pairingTimeout.CancelAfter(TimeSpan.FromSeconds(20));
            string response = await client.PairAsync(new IPEndPoint(pairing.Address, pairing.Port).ToString(), pairing.Port, code.Password, pairingTimeout.Token).ConfigureAwait(false);
            if (!AdbPairingCode.IsPairingSuccessful(response))
            {
                throw new IOException("无线调试配对失败，请重新生成二维码后扫描。");
            }
        }

        using CancellationTokenSource connectionTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectionTimeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            while (true)
            {
                IReadOnlyList<AdbMdnsService> services = await FindServicesAsync(connectionTimeout.Token).ConfigureAwait(false);
                foreach (AdbMdnsService service in services.Where(service => string.Equals(service.Type, AdbMdns.ConnectService, StringComparison.Ordinal) &&
                             service.Address.Equals(pairing.Address)))
                {
                    string endpoint = new IPEndPoint(service.Address, service.Port).ToString();
                    string response = await client.ConnectAsync(endpoint, service.Port, connectionTimeout.Token).ConfigureAwait(false);
                    if (!AdbPairingCode.IsConnectionSuccessful(response))
                    {
                        continue;
                    }

                    IEnumerable<DeviceData> devices = await client.GetDevicesAsync(connectionTimeout.Token).ConfigureAwait(false);
                    DeviceData? device = devices.FirstOrDefault(device => device.State == DeviceState.Online &&
                        (string.Equals(device.Serial, endpoint, StringComparison.Ordinal) ||
                         string.Equals(device.Serial, service.Instance + "." + AdbMdns.ConnectService, StringComparison.Ordinal)));
                    if (device is not null)
                    {
                        return new UsbDevice(device.Serial, string.IsNullOrWhiteSpace(device.Model) ? "无线调试设备" : device.Model.Replace('_', ' '),
                            UsbDeviceState.Online, IsWireless: true);
                    }
                }

                await Task.Delay(750, connectionTimeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new IOException("已配对，但未能连接无线调试设备。请确认手机和电脑处于同一局域网，并保持无线调试开启后重试。", ex);
        }
    }

    private async Task<IReadOnlyList<AdbMdnsService>> FindServicesAsync(CancellationToken ct)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        return await AdbMdns.QueryAsync(_serverEndpoint, timeout.Token).ConfigureAwait(false);
    }
}
