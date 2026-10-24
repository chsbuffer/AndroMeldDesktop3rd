using System.Net;

using Andromeld.Discovery;

namespace Andromeld.Desktop.Tests;

internal sealed class StubUsbDiscovery : IUsbDiscovery
{
    public Task<IReadOnlyList<UsbDevice>> FindAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<UsbDevice>>([]);
    public Task<IUsbConnection> OpenAsync(UsbDevice device, CancellationToken ct, IProgress<UsbConnectionProgress>? progress = null, Func<CancellationToken, Task>? confirmPrivacy = null) => throw new InvalidOperationException("No synthetic USB device is available.");

    public Task<IUsbConnection> OpenWirelessAsync(IPAddress address, CancellationToken ct, IProgress<UsbConnectionProgress>? progress = null,
        Func<CancellationToken, Task>? confirmPrivacy = null, Func<CancellationToken, Task<string>>? requestPairingCode = null) =>
        throw new InvalidOperationException("No synthetic wireless device is available.");
}
