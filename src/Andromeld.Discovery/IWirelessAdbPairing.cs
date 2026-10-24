using Andromeld.Protocol;

namespace Andromeld.Discovery;

public interface IWirelessAdbPairing
{
    Task<UsbDevice> PairAsync(AdbPairingCode code, CancellationToken ct);
}
