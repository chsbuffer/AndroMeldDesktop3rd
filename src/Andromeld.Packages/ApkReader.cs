using System.Globalization;
using System.IO.Compression;

namespace Andromeld.Packages;

internal static class ApkReader
{
    private const int MaxMetadataBytes = 64 * 1024 * 1024;

    public static PackageFile Read(string path, string archiveName)
    {
        using var stream = File.OpenRead(path);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var manifest = new AndroidResources(ReadEntry(zip.GetEntry("AndroidManifest.xml") ?? throw new InvalidDataException("APK 缺少 AndroidManifest.xml。")));
        var elements = manifest.ReadXml();
        var root = Array.Find(elements, static e => string.Equals(e.Name, "manifest", StringComparison.Ordinal)) ?? throw new InvalidDataException("APK 缺少 manifest 元素。");
        var resources = new AndroidResources(zip.GetEntry("resources.arsc") is { } table ? ReadEntry(table) : []);
        if (zip.GetEntry("resources.arsc") is not null) resources.ReadTable();
        string? Attribute(AndroidResources.Element? element, string name) => element?.Attributes.TryGetValue(name, out var value) == true ? resources.Resolve(value) : null;
        string package = Attribute(root, "package") ?? throw new InvalidDataException("APK 缺少包名。");
        if (package.Length > 255 || package is "." or ".." || package.Any(static c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_'))) throw new InvalidDataException("APK 包名无效。");
        var application = Array.Find(elements, static e => string.Equals(e.Name, "application", StringComparison.Ordinal));
        var sdk = Array.Find(elements, static e => string.Equals(e.Name, "uses-sdk", StringComparison.Ordinal));
        string[] permissions = elements.Where(static e => e.Name is "uses-permission" or "uses-permission-sdk-23")
            .Select(e => Attribute(e, "name")).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        long version = Number(Attribute(root, "versionCode"));
        version |= Number(Attribute(root, "versionCodeMajor")) << 32;
        byte[]? icon = null;
        PackageVectorIcon? vector = null;
        if (application?.Attributes.TryGetValue("icon", out var iconValue) == true)
        {
            icon = ReadIcon(zip, resources, iconValue, 0);
            if (icon is null) vector = AndroidVectorIcon.Read(zip, resources, iconValue);
        }
        PackageInfo info = new(package, Attribute(application, "label") ?? package, Attribute(root, "versionName") ?? "", version,
            checked((int)Number(Attribute(sdk, "minSdkVersion"), 1)), ParseSdk(Attribute(sdk, "targetSdkVersion")), Attribute(root, "split"),
            permissions, ApkCertificates.Read(stream, zip), icon, vector);
        string[] abis = zip.Entries.Select(static e => e.FullName.Split('/'))
            .Where(static p => p.Length >= 3 && string.Equals(p[0], "lib", StringComparison.Ordinal)).Select(static p => p[1]).Distinct(StringComparer.Ordinal).ToArray();
        return new(path, archiveName, stream.Length, info, abis);
    }

    private static long Number(string? value, long fallback = 0) => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long number) ? number : fallback;
    private static int? ParseSdk(string? value) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int sdk) ? sdk : null;

    internal static PackageInfo ReadCombinedIcon(PackageFile[] apks)
    {
        var main = Array.Find(apks, static a => string.IsNullOrEmpty(a.Info.SplitName)) ?? throw new InvalidDataException("缺少基础 APK。");
        if (main.Info.Icon is not null || main.Info.VectorIcon is not null || apks.Length == 1) return main.Info;
        using var mainZip = ZipFile.OpenRead(main.Path);
        var elements = new AndroidResources(ReadEntry(mainZip.GetEntry("AndroidManifest.xml") ?? throw new InvalidDataException("缺少 manifest。"))).ReadXml();
        var application = Array.Find(elements, static e => string.Equals(e.Name, "application", StringComparison.Ordinal));
        if (application?.Attributes.TryGetValue("icon", out var value) != true) return main.Info;
        var combined = new AndroidResources([]);
        foreach (var file in apks)
        {
            using var zip = ZipFile.OpenRead(file.Path);
            if (zip.GetEntry("resources.arsc") is not { } entry) continue;
            var table = new AndroidResources(ReadEntry(entry)); table.ReadTable(); combined.Merge(table);
        }
        foreach (var file in apks)
        {
            using var zip = ZipFile.OpenRead(file.Path);
            if (ReadIcon(zip, combined, value, 0) is { } bitmap) return main.Info with { Icon = bitmap };
            if (AndroidVectorIcon.Read(zip, combined, value) is { } vector) return main.Info with { VectorIcon = vector };
        }
        return main.Info;
    }

    private static byte[]? ReadIcon(ZipArchive zip, AndroidResources resources, AndroidResources.Value value, int depth)
    {
        if (depth > 4) return null;
        string[] candidates = resources.ResolveAll(value).Distinct(StringComparer.Ordinal).ToArray();
        foreach (string path in candidates.Reverse())
        {
            if (!IsBitmap(path) || zip.GetEntry(path) is not { Length: <= 4 * 1024 * 1024 } entry) continue;
            return ReadEntry(entry);
        }
        foreach (string path in candidates)
        {
            if (!path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || zip.GetEntry(path) is not { } xml) continue;
            var elements = new AndroidResources(ReadEntry(xml)).ReadXml();
            foreach (var layer in elements.Where(static e => string.Equals(e.Name, "foreground", StringComparison.Ordinal)))
                if (layer.Attributes.TryGetValue("drawable", out var drawable) && ReadIcon(zip, resources, drawable, depth + 1) is { } bitmap)
                    return bitmap;
            if (AndroidVectorIcon.Read(zip, resources, value) is not null) continue;
            foreach (var layer in elements.Where(static e => string.Equals(e.Name, "background", StringComparison.Ordinal)))
                if (layer.Attributes.TryGetValue("drawable", out var drawable) && ReadIcon(zip, resources, drawable, depth + 1) is { } bitmap)
                    return bitmap;
        }
        return null;
    }

    private static bool IsBitmap(string path) => Path.GetExtension(path).ToLowerInvariant() is ".png" or ".webp" or ".jpg" or ".jpeg";

    internal static byte[] ReadEntry(ZipArchiveEntry entry)
    {
        if (entry.Length > MaxMetadataBytes) throw new InvalidDataException("安装包元数据过大。");
        using var source = entry.Open();
        byte[] bytes = new byte[checked((int)entry.Length)];
        source.ReadExactly(bytes);
        if (source.ReadByte() != -1) throw new InvalidDataException("安装包元数据长度不一致。");
        return bytes;
    }
}
