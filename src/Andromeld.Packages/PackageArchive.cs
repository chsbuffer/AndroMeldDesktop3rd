using System.IO.Compression;

namespace Andromeld.Packages;

/// <summary>Private, disposable disk snapshot. Parsing/extraction runs on a worker; returned metadata is caller-owned.</summary>
public sealed class PackageArchive : IDisposable
{
    private const long MaxBytes = 4L * 1024 * 1024 * 1024;
    private readonly string _directory;
    private readonly BundleTable? _bundle;
    private PackageArchive(string directory, string name, string format, long size, PackageFile[] apks, ExpansionFile[] expansions, BundleTable? bundle)
    {
        _directory = directory; FileName = name; Format = format; Size = size; Apks = apks; Expansions = expansions;
        Info = ApkReader.ReadCombinedIcon(apks);
        _bundle = bundle;
    }

    public string FileName { get; }
    public string Format { get; }
    public long Size { get; }
    public PackageInfo Info { get; }
    public IReadOnlyList<PackageFile> Apks { get; }
    public IReadOnlyList<ExpansionFile> Expansions { get; }
    public static bool IsSupported(string path) => Path.GetExtension(path).ToLowerInvariant() is ".apk" or ".apks" or ".apkm" or ".xapk";

    public static Task<PackageArchive> OpenAsync(string path, CancellationToken ct) => Task.Run(() => Open(path, ct), ct);

    private static PackageArchive Open(string path, CancellationToken ct)
    {
        if (!IsSupported(path)) throw new NotSupportedException("支持 APK、APKS、APKM 和 XAPK 安装包。");
        string directory = Path.Combine(Path.GetTempPath(), "andromeld-package-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            string snapshot = Path.Combine(directory, "source");
            using (var source = File.OpenRead(path))
            using (var target = File.Create(snapshot)) Copy(source, target, source.Length, ct);
            long size = new FileInfo(snapshot).Length;
            List<PackageFile> apks = [];
            List<ExpansionFile> expansions = [];
            BundleTable? bundle = null;
            if (string.Equals(Path.GetExtension(path), ".apk", StringComparison.OrdinalIgnoreCase)) apks.Add(ApkReader.Read(snapshot, Path.GetFileName(path)));
            else
            {
                using var zip = ZipFile.OpenRead(snapshot);
                if (zip.GetEntry("toc.pb") is { } toc) bundle = new(ApkReader.ReadEntry(toc));
                long expanded = 0;
                foreach (var entry in zip.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    string extension = Path.GetExtension(entry.FullName);
                    if (!extension.Equals(".apk", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".obb", StringComparison.OrdinalIgnoreCase)) continue;
                    if (apks.Count + expansions.Count >= 512) throw new InvalidDataException("安装包包含过多文件。");
                    expanded = checked(expanded + entry.Length);
                    if (expanded > MaxBytes) throw new InvalidDataException("解压后的安装包不能超过 4 GiB。");
                    string extracted = Path.Combine(directory, (apks.Count + expansions.Count).ToString(System.Globalization.CultureInfo.InvariantCulture));
                    using (var source = entry.Open())
                    using (var target = File.Create(extracted)) Copy(source, target, entry.Length, ct);
                    if (extension.Equals(".apk", StringComparison.OrdinalIgnoreCase)) apks.Add(ApkReader.Read(extracted, entry.FullName));
                    else expansions.Add(new(extracted, entry.FullName, entry.Length));
                }
            }
            if (!apks.Exists(static a => string.IsNullOrEmpty(a.Info.SplitName))) throw new InvalidDataException("安装包缺少基础 APK。");
            var main = apks.First(static a => string.IsNullOrEmpty(a.Info.SplitName)).Info;
            if (apks.Exists(a => !string.Equals(a.Info.PackageName, main.PackageName, StringComparison.Ordinal) || a.Info.VersionCode != main.VersionCode)) throw new InvalidDataException("分包的包名或版本不一致。");
            foreach (var expansion in expansions)
            {
                string[] parts = expansion.Name.Split('/');
                if (parts.Length != 4 || !string.Equals(parts[0], "Android", StringComparison.Ordinal) || !string.Equals(parts[1], "obb", StringComparison.Ordinal) || !string.Equals(parts[2], main.PackageName, StringComparison.Ordinal) ||
                    parts[3].Length == 0 || parts[3].Any(static c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')))
                    throw new InvalidDataException("XAPK 的 OBB 路径无效。");
            }
            return new(directory, Path.GetFileName(path), Path.GetExtension(path).TrimStart('.').ToUpperInvariant(), size, apks.ToArray(), expansions.ToArray(), bundle);
        }
        catch
        {
            Directory.Delete(directory, recursive: true);
            throw;
        }
    }

    public IReadOnlyList<PackageFile> SelectApks(int sdk, IReadOnlyList<string> supportedAbis, int density = 0)
    {
        if (_bundle is not null)
        {
            var paths = _bundle.Select(sdk, supportedAbis, density);
            var files = Apks.Where(a => paths.Contains(a.ArchiveName)).OrderBy(static a => !string.IsNullOrEmpty(a.Info.SplitName)).ToArray();
            if (files.Length != paths.Count || files.Count(static f => string.IsNullOrEmpty(f.Info.SplitName)) != 1)
                throw new InvalidDataException("APKS 目录引用了缺失或无效的 APK。");
            return files;
        }
        var candidates = Apks.Where(a => string.IsNullOrEmpty(a.Info.SplitName) && a.Info.MinSdk <= sdk &&
            (a.Abis.Length == 0 || a.Abis.Intersect(supportedAbis, StringComparer.Ordinal).Any())).ToArray();
        var main = candidates.OrderByDescending(static a => a.ArchiveName.Equals("universal.apk", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(static a => a.Info.MinSdk).ThenBy(a => AbiRank(a, supportedAbis)).FirstOrDefault()
            ?? throw new NotSupportedException("安装包与目标设备的 Android 版本或 CPU 架构不兼容。");
        string? folder = Path.GetDirectoryName(main.ArchiveName);
        bool singleBase = Apks.Count(static a => string.IsNullOrEmpty(a.Info.SplitName)) == 1;
        var splits = Apks.Where(a => !string.IsNullOrEmpty(a.Info.SplitName) && (singleBase || string.Equals(Path.GetDirectoryName(a.ArchiveName), folder, StringComparison.Ordinal)) && a.Info.MinSdk <= sdk).ToArray();
        string? abi = supportedAbis.FirstOrDefault(a => (main.Abis.Length == 0 || main.Abis.Contains(a, StringComparer.Ordinal)) &&
            Array.Exists(splits, s => s.Abis.Contains(a, StringComparer.Ordinal)));
        if (abi is null && Array.Exists(splits, static s => s.Abis.Length > 0))
            throw new NotSupportedException("分包的 CPU 架构与目标设备不兼容。");
        var selected = splits.Where(s => s.Abis.Length == 0 || abi is not null && s.Abis.Contains(abi, StringComparer.Ordinal)).Prepend(main).ToArray();
        if (selected.Select(static a => a.Info.SplitName ?? "").Distinct(StringComparer.Ordinal).Count() != selected.Length)
            throw new NotSupportedException("安装包包含多个同名分包变体，请提供针对该设备导出的 APKS。");
        return selected;
    }

    private static int AbiRank(PackageFile file, IReadOnlyList<string> abis)
    {
        for (int i = 0; i < abis.Count; i++) if (file.Abis.Contains(abis[i], StringComparer.Ordinal)) return i;
        return int.MaxValue;
    }

    private static void Copy(Stream source, Stream target, long length, CancellationToken ct)
    {
        if (length is <= 0 or > MaxBytes) throw new InvalidDataException("安装包文件大小无效或超过 4 GiB。");
        byte[] buffer = new byte[128 * 1024];
        long copied = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int count = source.Read(buffer);
            if (count == 0) break;
            copied += count;
            if (copied > length) throw new InvalidDataException("安装包文件长度发生变化。");
            target.Write(buffer, 0, count);
        }
        if (copied != length) throw new InvalidDataException("安装包文件被截断。");
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
