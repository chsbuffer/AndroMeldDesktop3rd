using System.Text.Json.Serialization;

namespace Andromeld.Desktop.Services;

/// <summary>Restorable client size in DIP; fullscreen and minimized bounds are never stored.</summary>
public sealed record MirrorWindowSettings(double Width, double Height, bool Maximized)
{
    [JsonIgnore]
    public bool IsValid => double.IsFinite(Width) && double.IsFinite(Height) && Width > 0 && Height > 0;
}
