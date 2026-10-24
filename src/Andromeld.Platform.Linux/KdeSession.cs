namespace Andromeld.Platform.Linux;

/// <summary>Detects a KDE desktop session without depending on Avalonia or the application assembly.</summary>
public static class KdeSession
{
    public static bool IsKde { get; } =
        string.Equals(Environment.GetEnvironmentVariable("KDE_FULL_SESSION"), "true", StringComparison.OrdinalIgnoreCase) ||
        (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "").Split(':').Contains("KDE", StringComparer.OrdinalIgnoreCase);
}
