using System.Diagnostics;
using System.Text;

using Andromeld.Protocol;
using Andromeld.Testing;

namespace Andromeld.Protocol.Tests;

public static partial class Program
{
    private static async Task CheckInstallationAsync()
    {
        await using var phone = await MockPhone.StartAsync();
        await using var client = new AndromeldClient();
        await client.ConnectAsync(phone.Endpoint, "synthetic-installer", null, null, CancellationToken.None);
        phone.SimulatePackageInstaller = true;
        var target = await client.GetPackageInstallTargetAsync(CancellationToken.None);
        Check(target is { Sdk: 36 } && target.Abis.SequenceEqual(new[] { "arm64-v8a", "armeabi-v7a" }), "typed compatibility query decodes framed terminal output");
        string temporary = Path.Combine(Path.GetTempPath(), "andromeld-install-shell-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var apks = new[] { new byte[] { 0, 1, 2, 3 }, new byte[] { 10, 20, 30 } };
            if (!OperatingSystem.IsWindows())
            {
                string bin = Path.Combine(temporary, "bin"); Directory.CreateDirectory(bin);
                string pm = Path.Combine(bin, "pm");
                await File.WriteAllTextAsync(pm, """
                    #!/bin/sh
                    case "$1" in
                      install-create) echo 'Success: created install session [42]';;
                      install-write)
                        if [ -f /proc/self/fd/0 ]; then
                          echo 'cmd: Failure calling service package: Failed transaction (2147483646)'
                          exit 2
                        fi
                        cat > "$TEST_OUTPUT/$5"
                        actual=$(wc -c < "$TEST_OUTPUT/$5")
                        [ "$actual" -eq "$3" ] || exit 7
                        echo 'Success: streamed';;
                      install-commit) if [ "$TEST_REJECT" = 1 ]; then echo 'Failure [INSTALL_FAILED_UPDATE_INCOMPATIBLE]'; exit 1; fi; echo committed >> "$TEST_OUTPUT/events"; echo Success;;
                      install-abandon) echo abandoned >> "$TEST_OUTPUT/events";;
                      *) exit 8;;
                    esac
                    """);
                File.SetUnixFileMode(pm, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                bool reject = false;
                phone.InstallationShellHandler = async script =>
                {
                    foreach (var upload in phone.Uploads)
                    {
                        string remote = upload.Begin.GetProperty("destPath").GetString() + "/" + upload.Begin.GetProperty("name").GetString();
                        string local = remote.Replace("/storage/emulated/0", temporary + "/phone", StringComparison.Ordinal);
                        Directory.CreateDirectory(Path.GetDirectoryName(local)!);
                        await File.WriteAllBytesAsync(local, upload.Data);
                    }
                    var start = new ProcessStartInfo("/bin/sh") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
                    start.Environment["PATH"] = bin + ":" + start.Environment["PATH"];
                    start.Environment["TEST_OUTPUT"] = temporary;
                    start.Environment["TEST_REJECT"] = reject ? "1" : "0";
                    using var process = Process.Start(start)!;
                    await process.StandardInput.WriteAsync(script.Replace("/storage/emulated/0", temporary + "/phone", StringComparison.Ordinal));
                    process.StandardInput.Close();
                    string result = await process.StandardOutput.ReadToEndAsync();
                    string error = await process.StandardError.ReadToEndAsync();
                    await process.WaitForExitAsync();
                    if (process.ExitCode != 0) throw new IOException(error);
                    return result;
                };
                await client.InstallPackageAsync("org.example.test", apks.Select(static b => new PackageInstallFile(b.Length, () => new MemoryStream(b))).ToArray(),
                    [new("main.1.org.example.test.obb", 3, static () => new MemoryStream(new byte[] { 6, 7, 8 }))], null, CancellationToken.None);
                Check(File.ReadAllBytes(Path.Combine(temporary, "0.apk")).SequenceEqual(apks[0]) && File.ReadAllBytes(Path.Combine(temporary, "1.apk")).SequenceEqual(apks[1]), "real shell streams all uploaded split bytes without passing external-storage file descriptors to Android");
                Check(File.ReadAllText(Path.Combine(temporary, "events")).Trim() == "committed" && File.ReadAllBytes(Path.Combine(temporary, "phone/Android/obb/org.example.test/main.1.org.example.test.obb")).SequenceEqual(new byte[] { 6, 7, 8 }), "OBB copied and commit success is required before reporting installed");
                reject = true;
                try
                {
                    await client.InstallPackageAsync("org.example.test", [new(4, () => new MemoryStream(apks[0]))],
                        [new("main.1.org.example.test.obb", 3, static () => new MemoryStream(new byte[] { 9, 9, 9 }))], null, CancellationToken.None);
                    throw new InvalidOperationException("Accepted rejected install");
                }
                catch (IOException ex) { Check(ex.Message.Contains("INSTALL_FAILED_UPDATE_INCOMPATIBLE", StringComparison.Ordinal), "Android signature conflict is surfaced after successful upload"); }
                Check(File.ReadAllLines(Path.Combine(temporary, "events")).Last() == "abandoned", "failed install session is abandoned by shell trap");
                Check(File.ReadAllBytes(Path.Combine(temporary, "phone/Android/obb/org.example.test/main.1.org.example.test.obb")).SequenceEqual(new byte[] { 6, 7, 8 }), "rejected update preserves the installed application's OBB files");
            }
            phone.InstallationShellHandler = null;
            try
            {
                await client.InstallPackageAsync("..", [new(4, () => new MemoryStream(apks[0]))], [], null, CancellationToken.None);
                throw new InvalidOperationException("Accepted unsafe package name");
            }
            catch (ArgumentException) { Check(true, "package name cannot escape the OBB app directory"); }
            phone.RejectUploads = true;
            int before = phone.DeleteRequests.Count;
            try
            {
                await client.InstallPackageAsync("org.example.test", [new(4, () => new MemoryStream(apks[0]))], [], null, CancellationToken.None);
                throw new InvalidOperationException("Accepted failed upload");
            }
            catch (IOException) { Check(phone.DeleteRequests.Count == before + 1, "failed upload cleans the remote staging folder"); }
            using var stop = new CancellationTokenSource(); stop.Cancel();
            try
            {
                await client.InstallPackageAsync("org.example.test", [new(4, () => new MemoryStream(apks[0]))], [], null, stop.Token);
                throw new InvalidOperationException("Accepted canceled installation");
            }
            catch (OperationCanceledException) { Check(true, "cancellation prevents a new installation"); }
        }
        finally { Directory.Delete(temporary, recursive: true); }
    }
}
