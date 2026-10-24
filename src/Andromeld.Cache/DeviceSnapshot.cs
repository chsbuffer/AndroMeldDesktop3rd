namespace Andromeld.Cache;

internal sealed class DeviceSnapshot
{
    public string DeviceKey { get; set; } = "";
    public long UpdatedUtcTicks { get; set; }
}