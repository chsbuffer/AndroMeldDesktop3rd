using System.Text.Json;

namespace Andromeld.Protocol;

public sealed record UsbHelperMetadata(long VersionCode, string Sha256)
{
    public static Uri MetadataUri { get; } = new("https://app.andromeld.catchingnow.com/AndroMeld-WebUSB.apk.json");
    public static Uri ApkUri { get; } = new("https://app.andromeld.catchingnow.com/AndroMeld-WebUSB.apk");

    public static UsbHelperMetadata Parse(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("packageName", out JsonElement package) && package.ValueKind == JsonValueKind.String &&
                package.GetString() is UsbHelper.PackageName &&
                root.TryGetProperty("apk", out JsonElement apk) && apk.ValueKind == JsonValueKind.String &&
                apk.GetString() is "AndroMeld-WebUSB.apk" &&
                root.TryGetProperty("versionCode", out JsonElement version) && version.ValueKind == JsonValueKind.Number &&
                version.TryGetInt64(out long code) && code > 0 &&
                root.TryGetProperty("sha256", out JsonElement hash) && hash.ValueKind == JsonValueKind.String &&
                hash.GetString() is { Length: 64 } sha &&
                sha.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            {
                return new UsbHelperMetadata(code, sha);
            }
        }
        catch (JsonException)
        {
            /* Never expose remote payloads in errors. */
        }

        throw new IOException("官网 USB helper 元数据格式无效，请稍后重试。");
    }
}