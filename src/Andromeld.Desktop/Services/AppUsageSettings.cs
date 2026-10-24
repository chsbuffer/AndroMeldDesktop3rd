namespace Andromeld.Desktop.Services;

/// <summary>UI-owned preferences, partitioned by authenticated device and Android user/package.</summary>
public sealed class AppUsageSettings
{
    public HashSet<string> Pinned { get; init; } = [];
    public Dictionary<string, DateTimeOffset> LastOpened { get; init; } = [];
}