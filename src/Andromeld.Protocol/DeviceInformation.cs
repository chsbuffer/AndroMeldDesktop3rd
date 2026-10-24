namespace Andromeld.Protocol;

/// <summary>An immutable device-info reply; battery/storage fields may be unavailable.</summary>
public sealed record DeviceInformation(
    string? Id,
    string? Name,
    string? Android,
    int? Sdk,
    DeviceBattery? Battery,
    DeviceStorage? Storage);