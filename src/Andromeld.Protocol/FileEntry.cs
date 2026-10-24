using System.Globalization;
using System.Text.Json.Serialization;

namespace Andromeld.Protocol;

public sealed record FileEntry(string Name, bool IsDir, long Size, long Mtime, string? Mime)
{
    [JsonIgnore]
    public string Kind => IsDir ? "文件夹" : Mime ?? "文件";

    [JsonIgnore]
    public string SizeText => IsDir ? "—" : FormatSize(Size);

    [JsonIgnore]
    public string Modified => Mtime > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(Mtime).ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "—";

    public static string FormatSize(long bytes)
    {
        return bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1048576 => $"{bytes / 1024D:0.#} KB",
            < 1073741824 => $"{bytes / 1048576D:0.#} MB",
            _ => $"{bytes / 1073741824D:0.#} GB"
        };
    }
}