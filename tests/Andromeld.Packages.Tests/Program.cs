using Andromeld.Packages;
using Andromeld.Testing;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Andromeld.Packages.Tests;

internal static class Program
{
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
    }

    public static async Task Main(string[] args)
    {
        if (args.Length > 0)
        {
            foreach (string path in args)
            {
                using var archive = await PackageArchive.OpenAsync(path, CancellationToken.None);
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(archive.Info with { Icon = null }));
                Console.WriteLine($"APKs={archive.Apks.Count}, icon={archive.Info.Icon?.Length ?? 0}");
            }
            return;
        }
        string temporary = Path.Combine(Path.GetTempPath(), "andromeld-packages-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            string path = Path.Combine(temporary, "测试 app.APK");
            await File.WriteAllBytesAsync(path, PackageFixture.Apk());
            string snapshot;
            using (var archive = await PackageArchive.OpenAsync(path, CancellationToken.None))
            {
                Check(archive.Info is { PackageName: "org.example.seaglass", Label: "Seaglass", VersionName: "2.4.1", VersionCode: 241, MinSdk: 29, TargetSdk: 36 }, "binary manifest exposes app details and SDK requirements");
                Check(archive.Info.Permissions.Contains("android.permission.CAMERA") && archive.Info.Permissions.Length == 3, "binary permission attributes are decoded");
                Check(archive.Info.Icon is { Length: > 0 }, "ARSC references resolve the application label and packaged icon");
                snapshot = archive.Apks[0].Path;
                await File.WriteAllBytesAsync(path, [1, 2, 3]);
                Check(new FileInfo(snapshot).Length > 3 && archive.SelectApks(36, ["arm64-v8a"]).Count == 1, "confirmation uses immutable private snapshot when original changes");
                try { archive.SelectApks(28, ["arm64-v8a"]); throw new InvalidOperationException("Accepted old SDK"); }
                catch (NotSupportedException) { Check(true, "incompatible SDK rejected before upload"); }
            }
            Check(!File.Exists(snapshot), "closing package releases snapshot files");
            foreach (string layout in new[] { "utf16", "sparse", "offset16", "compact" })
            {
                path = Path.Combine(temporary, "resources.apk");
                await File.WriteAllBytesAsync(path, PackageFixture.Apk(resourceLayout: layout));
                using var archive = await PackageArchive.OpenAsync(path, CancellationToken.None);
                Check(archive.Info is { Label: "Seaglass", Icon.Length: > 0 }, layout + " ARSC resolves the same app label and icon");
            }
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=Seaglass", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            byte[] der = certificate.Export(X509ContentType.Cert);
            path = Path.Combine(temporary, "v1.apk");
            await File.WriteAllBytesAsync(path, PackageFixture.WithV1Certificate(PackageFixture.Apk(), der));
            using (var archive = await PackageArchive.OpenAsync(path, CancellationToken.None))
                Check(archive.Info.Certificates is [{ Subject: "CN=Seaglass" }] && archive.Info.Certificates[0].Sha256 == Convert.ToHexString(SHA256.HashData(der)), "JAR v1 PKCS7 exposes the embedded certificate identity");
            path = Path.Combine(temporary, "vector.apk");
            await File.WriteAllBytesAsync(path, PackageFixture.Apk(vectorIcon: true));
            using (var archive = await PackageArchive.OpenAsync(path, CancellationToken.None))
                Check(archive.Info is { Icon: null, VectorIcon: { Width: 64, Height: 64, Paths: [{ Color: 0xff38a9af }] } }, "vector icon paths and colors decode through the resource table");
            path = Path.Combine(temporary, "cycle.apk");
            await File.WriteAllBytesAsync(path, PackageFixture.Apk(resourceLayout: "cycle"));
            using (var archive = await PackageArchive.OpenAsync(path, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)))
                Check(archive.Info is { Label: "org.example.seaglass", Icon: null, VectorIcon: null }, "cyclic resource references terminate and fall back to package identity");
            foreach (uint scheme in new uint[] { 0x7109871a, 0xf05368c0, 0x1b93ad61 })
            {
                path = Path.Combine(temporary, "certificate.apk");
                await File.WriteAllBytesAsync(path, PackageFixture.WithSigningCertificate(PackageFixture.Apk(), der, scheme));
                using var archive = await PackageArchive.OpenAsync(path, CancellationToken.None);
                Check(archive.Info.Certificates is [{ Subject: "CN=Seaglass" }] && archive.Info.Certificates[0].Sha256 == Convert.ToHexString(SHA256.HashData(der)), "APK v2/v3 signing block exposes certificate identity without claiming verification");
            }
            foreach (string extension in new[] { "apks", "apkm", "xapk" })
            {
                path = Path.Combine(temporary, "app." + extension);
                await File.WriteAllBytesAsync(path, PackageFixture.Archive(("base.apk", PackageFixture.Apk()),
                    ("split.arm64.apk", PackageFixture.Apk("config.arm64_v8a", abi: "arm64-v8a")),
                    ("split.x86.apk", PackageFixture.Apk("config.x86", abi: "x86")),
                    ("split.en.apk", PackageFixture.Apk("config.en"))));
                using var archive = await PackageArchive.OpenAsync(path, CancellationToken.None);
                var selected = archive.SelectApks(36, ["arm64-v8a", "armeabi-v7a"]);
                Check(selected.Count == 3 && !selected.Any(static f => f.Abis.Contains("x86")), extension + " installs base and resource splits with compatible native ABI");
            }
            path = Path.Combine(temporary, "split-icon.apkm");
            await File.WriteAllBytesAsync(path, PackageFixture.Archive(("base.apk", PackageFixture.Apk(includeIcon: false)), ("dpi/config.apk", PackageFixture.Apk("config.xxhdpi"))));
            using (var archive = await PackageArchive.OpenAsync(path, CancellationToken.None))
                Check(archive.Info.Icon is { Length: > 0 } && archive.SelectApks(36, ["arm64-v8a"]).Count == 2, "application icon and split in a subdirectory resolve across APK resources");
            path = Path.Combine(temporary, "bundle.apks");
            await File.WriteAllBytesAsync(path, PackageFixture.Archive(("standalones/standalone-x86.apk", PackageFixture.Apk(abi: "x86")),
                ("standalones/standalone-arm64.apk", PackageFixture.Apk(abi: "arm64-v8a")), ("splits/base-master.apk", PackageFixture.Apk())));
            using (var archive = await PackageArchive.OpenAsync(path, CancellationToken.None))
                Check(archive.SelectApks(36, ["arm64-v8a"])[0].Abis.Contains("arm64-v8a"), "APKS standalone variant matches target ABI");
            await File.WriteAllBytesAsync(path, PackageFixture.Archive(("toc.pb", PackageFixture.BundleTable()),
                ("old/base.apk", PackageFixture.Apk()), ("old/arm64.apk", PackageFixture.Apk("config.arm64_v8a", abi: "arm64-v8a")), ("old/x86.apk", PackageFixture.Apk("config.x86", abi: "x86")),
                ("new/base.apk", PackageFixture.Apk(minSdk: 35)), ("new/arm64.apk", PackageFixture.Apk("config.arm64_v8a", minSdk: 35, abi: "arm64-v8a")), ("new/x86.apk", PackageFixture.Apk("config.x86", minSdk: 35, abi: "x86"))));
            using (var archive = await PackageArchive.OpenAsync(path, CancellationToken.None))
            {
                var modern = archive.SelectApks(36, ["arm64-v8a"]);
                var older = archive.SelectApks(34, ["x86"]);
                Check(modern.Count == 2 && modern.All(static a => a.ArchiveName.StartsWith("new/", StringComparison.Ordinal)) && modern[1].Abis.Contains("arm64-v8a"), "bundletool toc chooses one SDK variant and its compatible ABI splits");
                Check(older.Count == 2 && older.All(static a => a.ArchiveName.StartsWith("old/", StringComparison.Ordinal)) && older[1].Abis.Contains("x86"), "bundletool SDK alternatives enforce exclusive variant ranges");
            }
            path = Path.Combine(temporary, "game.xapk");
            await File.WriteAllBytesAsync(path, PackageFixture.Archive(("base.apk", PackageFixture.Apk()), ("Android/obb/org.example.seaglass/main.241.org.example.seaglass.obb", new byte[] { 4, 5, 6 })));
            using (var archive = await PackageArchive.OpenAsync(path, CancellationToken.None))
                Check(archive.Expansions.Count == 1 && File.ReadAllBytes(archive.Expansions[0].Path).SequenceEqual(new byte[] { 4, 5, 6 }), "XAPK preserves validated OBB payload");
            await RejectAsync(temporary, "traversal.xapk", PackageFixture.Archive(("base.apk", PackageFixture.Apk()), ("../../escape.obb", new byte[] { 1 })), "unsafe OBB path rejected");
            await RejectAsync(temporary, "mixed.apkm", PackageFixture.Archive(("base.apk", PackageFixture.Apk()), ("split.apk", PackageFixture.Apk("config.en", package: "other.app"))), "mixed package identities rejected");
            await RejectAsync(temporary, "missing.apks", PackageFixture.Archive(("split.apk", PackageFixture.Apk("config.en"))), "missing base APK rejected");
            await RejectAsync(temporary, "unsafe-name.apk", PackageFixture.Apk(package: ".."), "invalid package name cannot escape expansion directory");
            path = Path.Combine(temporary, "incompatible.apkm");
            await File.WriteAllBytesAsync(path, PackageFixture.Archive(("base.apk", PackageFixture.Apk()), ("split.apk", PackageFixture.Apk("config.x86", abi: "x86"))));
            using (var archive = await PackageArchive.OpenAsync(path, CancellationToken.None))
            {
                try { archive.SelectApks(36, ["arm64-v8a"]); throw new InvalidOperationException("Accepted incompatible split ABI"); }
                catch (NotSupportedException) { Check(true, "incompatible native splits are rejected before uploading an incomplete base"); }
            }
            await RejectAsync(temporary, "broken.apk", [1, 2, 3], "invalid archive rejected");
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            try { await PackageArchive.OpenAsync(path, canceled.Token); throw new InvalidOperationException("Accepted cancellation"); }
            catch (OperationCanceledException) { Check(true, "canceled package load never starts extraction"); }
        }
        finally { Directory.Delete(temporary, recursive: true); }
    }

    private static async Task RejectAsync(string directory, string name, byte[] bytes, string description)
    {
        string path = Path.Combine(directory, name); await File.WriteAllBytesAsync(path, bytes);
        try { using var archive = await PackageArchive.OpenAsync(path, CancellationToken.None); throw new InvalidOperationException("Accepted invalid archive"); }
        catch (InvalidDataException) { Check(true, description); }
    }
}
