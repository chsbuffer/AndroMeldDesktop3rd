namespace Andromeld.Cache;

internal sealed class CachedApplication
{
    public string DeviceKey { get; set; } = "";
    public string PackageName { get; set; } = "";
    public int UserId { get; set; }
    public string DisplayName { get; set; } = "";
    public bool IsSystemApp { get; set; }
    public int AppCategory { get; set; }
    public bool Game { get; set; }
    public byte[]? Png { get; set; }
    public long? IconUpdatedUtcTicks { get; set; }
}