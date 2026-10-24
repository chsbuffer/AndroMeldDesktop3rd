namespace Andromeld.Desktop.Services;

public sealed class MirrorDisplaySettings
{
    public IDictionary<string, int> ZoomPercent { get; init; } = new Dictionary<string, int>(StringComparer.Ordinal);
    public IDictionary<string, bool> ImmersiveTitleBars { get; init; } = new Dictionary<string, bool>(StringComparer.Ordinal);
    public IDictionary<string, bool> ScreenCameras { get; init; } = new Dictionary<string, bool>(StringComparer.Ordinal);
    public IDictionary<string, MirrorWindowSettings> Windows { get; init; } = new Dictionary<string, MirrorWindowSettings>(StringComparer.Ordinal);
    public ISet<string> CompatiblePackages { get; init; } = new HashSet<string>(StringComparer.Ordinal);
    public bool AllAppsCompatible { get; set; }
}
