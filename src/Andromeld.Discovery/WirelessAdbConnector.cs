using System.Net;
using System.Net.Sockets;

using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.Exceptions;
using AdvancedSharpAdbClient.Models;

namespace Andromeld.Discovery;

/// <summary>
///     Connects the ADB server to a phone's wireless-debugging port. Android 11+ publishes random
///     connect/pairing ports over mDNS; the classic 5555 port remains a fallback for OEM builds.
/// </summary>
internal sealed class WirelessAdbConnector(AdbClient client, EndPoint serverEndpoint, UsbPreparation preparation)
{
    private const int ClassicAdbPort = 5555;
    private static readonly TimeSpan PairingServiceTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan ConnectServiceTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DeviceOnlineTimeout = TimeSpan.FromSeconds(60);

    public async Task<string> ConnectAsync(IPAddress address, Func<CancellationToken, Task<string>>? requestPairingCode, CancellationToken ct)
    {
        preparation.Report(UsbConnectionStage.FindingWirelessService);
        IReadOnlyList<AdbMdnsService> services = await QueryMdnsAsync(ct).ConfigureAwait(false);
        List<int> candidates = ConnectPorts(services, address);
        bool serviceDiscovered = candidates.Count > 0;
        if (!candidates.Contains(ClassicAdbPort))
        {
            candidates.Add(ClassicAdbPort);
        }

        (string? serial, bool pairingRequired) = await TryConnectPortsAsync(address, candidates, ct).ConfigureAwait(false);
        if (serial is not null)
        {
            return serial;
        }

        if (!pairingRequired && !serviceDiscovered)
        {
            // The ADB server's mDNS cache may still be warming up when the first query runs.
            preparation.Report(UsbConnectionStage.FindingWirelessService);
            List<int> discovered = await WaitForConnectPortsAsync(address, ct).ConfigureAwait(false);
            if (discovered.Count > 0)
            {
                (serial, pairingRequired) = await TryConnectPortsAsync(address, discovered, ct).ConfigureAwait(false);
                if (serial is not null)
                {
                    return serial;
                }
            }
        }

        if (!pairingRequired)
        {
            throw new IOException("无法连接手机的无线调试。请确认已开启『无线调试』、手机与电脑位于同一网络，且 IP 地址正确。");
        }

        if (requestPairingCode is null)
        {
            throw new IOException("手机要求先完成无线调试配对，但当前操作无法请求配对码。");
        }

        preparation.Report(UsbConnectionStage.WaitingForWirelessPairing);
        string code = (await requestPairingCode(ct).ConfigureAwait(false)).Trim();
        ct.ThrowIfCancellationRequested();
        if (code.Length is not 6 || !code.All(char.IsAsciiDigit))
        {
            throw new IOException("无线调试配对码应为 6 位数字。");
        }

        preparation.Report(UsbConnectionStage.FindingWirelessPairing);
        int pairingPort = await WaitForPairingPortAsync(address, ct).ConfigureAwait(false);
        preparation.Report(UsbConnectionStage.PairingWireless);
        await PairAsync(address, pairingPort, code, ct).ConfigureAwait(false);

        services = await QueryMdnsAsync(ct).ConfigureAwait(false);
        List<int> retryPorts = ConnectPorts(services, address);
        if (retryPorts.Count == 0)
        {
            retryPorts.AddRange(candidates);
        }

        (serial, _) = await TryConnectPortsAsync(address, retryPorts, ct).ConfigureAwait(false);
        return serial ?? throw new IOException("无线调试配对已完成，但仍无法连接手机。请在手机上重新打开『无线调试』后重试。");
    }

    private async Task<(string? Serial, bool PairingRequired)> TryConnectPortsAsync(IPAddress address, IEnumerable<int> ports, CancellationToken ct)
    {
        foreach (int port in ports.Distinct())
        {
            ConnectOutcome outcome = await TryConnectAsync(address, port, ct).ConfigureAwait(false);
            if (outcome.Serial is not null)
            {
                return (outcome.Serial, false);
            }

            if (outcome.PairingRequired)
            {
                return (null, true);
            }
        }

        return (null, false);
    }

    private async Task<List<int>> WaitForConnectPortsAsync(IPAddress address, CancellationToken ct)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(ConnectServiceTimeout);
        try
        {
            while (true)
            {
                IReadOnlyList<AdbMdnsService> services = await QueryMdnsAsync(deadline.Token).ConfigureAwait(false);
                List<int> ports = ConnectPorts(services, address);
                if (ports.Count > 0)
                {
                    return ports;
                }

                await Task.Delay(500, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new IOException("未发现无线调试连接服务。请确认手机上的『无线调试』已开启、手机与电脑位于同一网络，且 IP 地址正确。", ex);
        }
    }

    private async Task<ConnectOutcome> TryConnectAsync(IPAddress address, int port, CancellationToken ct)
    {
        string serial = FormatHostPort(address, port);
        preparation.Report(UsbConnectionStage.ConnectingWireless);
        string message;
        try
        {
            message = await UsbPreparation.WithTimeoutAsync(
                token => client.ConnectAsync(serial, ClassicAdbPort, token), 15, ct).ConfigureAwait(false);
        }
        catch (AdbException ex) when (ex.Message.Contains("authenticate", StringComparison.OrdinalIgnoreCase))
        {
            return ConnectOutcome.AuthenticationRequired;
        }
        catch (AdbException)
        {
            return ConnectOutcome.Failed;
        }

        // A TLS adbd that does not trust our host key reports "failed to authenticate" and
        // needs the six-digit pairing flow. "connected" means transport authentication
        // succeeded; devices-l then separates ready ("device") from the phone's RSA
        // authorization prompt ("unauthorized"), which never asks for a pairing code.
        if (message.Contains("failed to authenticate", StringComparison.OrdinalIgnoreCase))
        {
            return ConnectOutcome.AuthenticationRequired;
        }

        if (!message.Contains("connected", StringComparison.OrdinalIgnoreCase))
        {
            return ConnectOutcome.Failed;
        }

        await WaitForOnlineAsync(serial, ct).ConfigureAwait(false);
        return ConnectOutcome.Connected(serial);
    }

    private async Task WaitForOnlineAsync(string serial, CancellationToken ct)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(DeviceOnlineTimeout);
        bool unauthorized = false;
        try
        {
            while (true)
            {
                IEnumerable<DeviceData> devices = await client.GetDevicesAsync(deadline.Token).ConfigureAwait(false);
                DeviceData? device = devices.FirstOrDefault(candidate => string.Equals(candidate.Serial, serial, StringComparison.Ordinal));
                if (device is { State: DeviceState.Online })
                {
                    return;
                }

                if (device is { State: DeviceState.Unauthorized or DeviceState.Authorizing } && !unauthorized)
                {
                    unauthorized = true;
                    preparation.Report(UsbConnectionStage.WaitingForWirelessAuthorization);
                }

                await Task.Delay(500, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new IOException(unauthorized
                ? "等待手机允许无线调试超时。请解锁手机并在调试授权提示中允许这台电脑，然后重试。"
                : "无线调试设备未上线，请重新开启手机上的『无线调试』后重试。", ex);
        }
    }

    private async Task<int> WaitForPairingPortAsync(IPAddress address, CancellationToken ct)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(PairingServiceTimeout);
        try
        {
            while (true)
            {
                IReadOnlyList<AdbMdnsService> services = await QueryMdnsAsync(deadline.Token).ConfigureAwait(false);
                int? pairingPort = services
                    .Where(service => string.Equals(service.Type, AdbMdns.PairingService, StringComparison.Ordinal) &&
                                      IPAddress.Equals(service.Address, address))
                    .Select(static service => (int?)service.Port)
                    .FirstOrDefault();
                if (pairingPort is not null)
                {
                    return pairingPort.Value;
                }

                await Task.Delay(1000, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new IOException("未发现无线调试配对服务。请在手机上打开『无线调试 → 使用配对码配对设备』并保持窗口开启，然后重试。", ex);
        }
    }

    private async Task PairAsync(IPAddress address, int port, string code, CancellationToken ct)
    {
        string serial = FormatHostPort(address, port);
        try
        {
            await UsbPreparation.WithTimeoutAsync(
                token => client.PairAsync(serial, port, code, token), 30, ct).ConfigureAwait(false);
        }
        catch (AdbException ex)
        {
            throw new IOException("请确认配对码正确，并保持手机上的配对窗口开启后重试。", ex);
        }
    }

    private async Task<IReadOnlyList<AdbMdnsService>> QueryMdnsAsync(CancellationToken ct)
    {
        try
        {
            return await UsbPreparation.WithTimeoutAsync(
                token => AdbMdns.QueryAsync(serverEndpoint, token), 3, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is AdbException or IOException or SocketException && !ct.IsCancellationRequested)
        {
            return [];
        }
    }

    private static List<int> ConnectPorts(IReadOnlyList<AdbMdnsService> services, IPAddress address)
    {
        return services.Where(service => string.Equals(service.Type, AdbMdns.ConnectService, StringComparison.Ordinal) &&
                                         IPAddress.Equals(service.Address, address))
            .Select(static service => service.Port).Distinct().ToList();
    }

    private static string FormatHostPort(IPAddress address, int port)
    {
        return address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]:{port}" : $"{address}:{port}";
    }

    private readonly record struct ConnectOutcome(string? Serial, bool PairingRequired)
    {
        public static ConnectOutcome Failed => default;
        public static ConnectOutcome AuthenticationRequired => new(null, true);
        public static ConnectOutcome Connected(string serial) => new(serial, false);
    }
}
