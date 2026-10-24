using System.Globalization;
using System.Reflection;

namespace Andromeld.Desktop.Services;

public static class ApplicationInfo
{
    private static readonly Dictionary<string, string?> Metadata = typeof(ApplicationInfo).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().ToDictionary(static attribute => attribute.Key, static attribute => attribute.Value, StringComparer.Ordinal);

    public static string Version => Metadata.GetValueOrDefault("GitCommitHash") ?? "未知";

    // Store only Unix seconds and display UTC, independent of developer, build and runtime time zones.
    public static string BuildDate { get; } = long.TryParse(Metadata.GetValueOrDefault("GitCommitTimestamp"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var timestamp) &&
        timestamp >= DateTimeOffset.MinValue.ToUnixTimeSeconds() && timestamp <= DateTimeOffset.MaxValue.ToUnixTimeSeconds()
        ? DateTimeOffset.FromUnixTimeSeconds(timestamp).ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture) : "未知";

    public static Uri AndromeldWebsite { get; } = new("https://andromeld.catchingnow.com/");

    public static Uri DownloadChannel { get; } = new("https://t.me/chsbuffer_channel");
}
