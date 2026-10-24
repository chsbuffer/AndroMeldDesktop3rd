using System.Net;

namespace Andromeld.Discovery;

/// <summary>Each operation owns its ADB client. Returned snapshots have no thread affinity.</summary>
public interface IUsbDiscovery
{
    Task<IReadOnlyList<UsbDevice>> FindAsync(CancellationToken ct);

    /// <summary>Progress may originate on a worker. The consent callback is awaited without a timeout and must honor cancellation.</summary>
    Task<IUsbConnection> OpenAsync(UsbDevice device, CancellationToken ct, IProgress<UsbConnectionProgress>? progress = null,
        Func<CancellationToken, Task>? confirmPrivacy = null);

    /// <summary>
    ///     Connects the ADB server to the phone's wireless-debugging port, then reuses the USB helper/bootstrap/forward
    ///     flow. <paramref name="requestPairingCode" /> runs on a worker and returns the 6-digit code shown by the phone;
    ///     it is only called when the phone rejects the ADB authentication.
    /// </summary>
    Task<IUsbConnection> OpenWirelessAsync(IPAddress address, CancellationToken ct, IProgress<UsbConnectionProgress>? progress = null,
        Func<CancellationToken, Task>? confirmPrivacy = null, Func<CancellationToken, Task<string>>? requestPairingCode = null);
}
