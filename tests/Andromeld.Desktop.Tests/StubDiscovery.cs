using System.Net;
using Andromeld.Discovery;

namespace Andromeld.Desktop.Tests;

// UI-owned deterministic discovery source; tests do not discover real LAN devices.
internal sealed class StubDiscovery : ILanDiscovery
{
    public event EventHandler? DevicesChanged;
    public IReadOnlyList<LanDevice> Devices { get; private set; } = [];

    public void Start()
    {
        Devices = [new("synthetic", "synthetic", "模拟手机", [new(IPAddress.Loopback, 12969, "test", "test")])];
        DevicesChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Refresh() => Start();
    public void SetDevices(params LanDevice[] devices)
    {
        Devices = devices;
        DevicesChanged?.Invoke(this, EventArgs.Empty);
    }
    public void Dispose() => Devices = [];
}
