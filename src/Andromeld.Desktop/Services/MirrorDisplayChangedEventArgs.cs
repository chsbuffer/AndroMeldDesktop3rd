namespace Andromeld.Desktop.Services;

public sealed class MirrorDisplayChangedEventArgs(string deviceKey) : EventArgs
{
    public string DeviceKey { get; } = deviceKey;
}