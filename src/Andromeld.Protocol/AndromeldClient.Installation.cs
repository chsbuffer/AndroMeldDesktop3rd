using System.Globalization;
using System.Text;

namespace Andromeld.Protocol;

public sealed partial class AndromeldClient
{
    public bool SupportsPackageInstallation => Capabilities.Contains("fs") && Capabilities.Contains("terminal");

    public async Task<PackageInstallTarget> GetPackageInstallTargetAsync(CancellationToken ct)
    {
        var terminal = await OpenTerminalAsync(80, 24, ct).ConfigureAwait(false);
        await using var terminalLease = terminal.ConfigureAwait(false);
        string output = await RunInstallShellAsync(terminal, "getprop ro.build.version.sdk\ngetprop ro.product.cpu.abilist\nwm density\n", ct).ConfigureAwait(false);
        string[] lines = output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2 || !int.TryParse(lines[0], NumberStyles.None, CultureInfo.InvariantCulture, out int sdk) || sdk < 1)
            throw new IOException("无法读取设备的安装兼容性信息。");
        int density = 0;
        foreach (string line in lines.Skip(2))
        {
            int colon = line.LastIndexOf(':');
            if (colon >= 0 && int.TryParse(line.AsSpan(colon + 1).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int parsed)) density = parsed;
        }
        return new(sdk, lines[1].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries), density);
    }

    /// <summary>Uses existing fs and PTY capabilities. Success requires Android's install-commit to succeed.</summary>
    public async Task InstallPackageAsync(string packageName, IReadOnlyList<PackageInstallFile> apks,
        IReadOnlyList<PackageExpansionFile> expansions, IProgress<PackageInstallProgress>? progress, CancellationToken ct)
    {
        if (!SupportsPackageInstallation) throw new NotSupportedException("设备需要同时支持文件传输和终端才能安装应用。");
        ArgumentException.ThrowIfNullOrWhiteSpace(packageName);
        if (packageName is "." or ".." || packageName.Any(static c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_')))
            throw new ArgumentException("无效的应用包名。", nameof(packageName));
        if (apks.Count is < 1 or > 512 || expansions.Count > 64) throw new ArgumentOutOfRangeException(nameof(apks));
        if (apks.Any(static a => a.Size is <= 0 or > 4L * 1024 * 1024 * 1024) || expansions.Any(static a => a.Size is <= 0 or > 4L * 1024 * 1024 * 1024))
            throw new ArgumentException("安装文件大小无效。", nameof(apks));
        if (expansions.Any(static file => !FileNameRules.IsValid(file.Name) || !file.Name.EndsWith(".obb", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("无效的 OBB 文件名。", nameof(expansions));
        long total = checked(apks.Sum(static a => a.Size) + expansions.Sum(static a => a.Size));
        if (total > 4L * 1024 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(apks), "安装文件总大小不能超过 4 GiB。");
        var terminal = await OpenTerminalAsync(80, 24, ct).ConfigureAwait(false);
        await using var terminalLease = terminal.ConfigureAwait(false);
        var roots = await GetRootsAsync(ct).ConfigureAwait(false);
        string root = roots.FirstOrDefault()?.Path ?? throw new IOException("设备没有可写入的存储空间。");
        string name = ".andromeld-install-" + Guid.NewGuid().ToString("N");
        string staging = root.TrimEnd('/') + "/" + name;
        await CreateDirectoryAsync(root, name, ct).ConfigureAwait(false);
        try
        {
            long sent = 0;
            for (int i = 0; i < apks.Count; i++)
            {
                var file = apks[i];
                using Stream source = file.OpenRead();
                await UploadAsync($"{i.ToString(CultureInfo.InvariantCulture)}.apk", file.Size, source, staging, null,
                    new InstallUploadProgress(progress, sent, total), ct).ConfigureAwait(false);
                sent += file.Size;
            }
            for (int i = 0; i < expansions.Count; i++)
            {
                var file = expansions[i];
                using Stream source = file.OpenRead();
                await UploadAsync($"{i.ToString(CultureInfo.InvariantCulture)}.obb", file.Size, source, staging, null,
                    new InstallUploadProgress(progress, sent, total), ct).ConfigureAwait(false);
                sent += file.Size;
            }
            progress?.Report(new("正在等待 Android 安装…", total, total));
            StringBuilder script = new();
            script.AppendLine("set -e");
            script.AppendLine("session=''");
            script.AppendLine("trap '[ -z \"$session\" ] || pm install-abandon \"$session\" >/dev/null 2>&1' EXIT HUP INT TERM");
            script.Append("result=$(pm install-create -r -S ").Append(apks.Sum(static a => a.Size).ToString(CultureInfo.InvariantCulture)).AppendLine(") || { printf '%s\\n' \"$result\"; exit 1; }");
            script.AppendLine("printf '%s\\n' \"$result\"");
            script.AppendLine("session=${result##*\\[}; session=${session%\\]*}");
            script.AppendLine("case \"$session\" in ''|*[!0-9]*) session=''; exit 1;; esac");
            // cmd passes stdin through Binder. Stream through a pipe so system_server never receives
            // an external-storage file descriptor that its SELinux domain cannot read.
            for (int i = 0; i < apks.Count; i++)
                script.Append("cat ").Append(QuoteShell(staging + "/" + i.ToString(CultureInfo.InvariantCulture) + ".apk"))
                    .Append(" | pm install-write -S ").Append(apks[i].Size.ToString(CultureInfo.InvariantCulture))
                    .Append(" \"$session\" ").Append(i.ToString(CultureInfo.InvariantCulture)).AppendLine(".apk -");
            script.AppendLine("pm install-commit \"$session\"");
            script.AppendLine("session=''");
            await RunInstallShellAsync(terminal, script.ToString(), ct).ConfigureAwait(false);
            // Preserve an existing app's expansion files when Android rejects an update.
            if (expansions.Count > 0)
            {
                script.Clear().AppendLine("set -e");
                string destination = root.TrimEnd('/') + "/Android/obb/" + packageName;
                script.Append("mkdir -p ").Append(QuoteShell(destination)).AppendLine();
                for (int i = 0; i < expansions.Count; i++)
                    script.Append("cp ").Append(QuoteShell(staging + "/" + i.ToString(CultureInfo.InvariantCulture) + ".obb"))
                        .Append(' ').Append(QuoteShell(destination + "/" + expansions[i].Name)).AppendLine();
                try { await RunInstallShellAsync(terminal, script.ToString(), ct).ConfigureAwait(false); }
                catch (IOException ex) { throw new IOException("应用已安装，但 OBB 扩展数据复制失败：" + ex.Message, ex); }
            }
        }
        finally
        {
            await terminal.DisposeAsync().ConfigureAwait(false);
            try
            {
                using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(5));
                await DeleteAsync([staging], cleanup.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or TimeoutException or System.Net.WebSockets.WebSocketException or ObjectDisposedException)
            {
                // A lost connection can leave this uniquely named staging folder on the phone.
            }
        }
    }

    private sealed class InstallUploadProgress(IProgress<PackageInstallProgress>? progress, long offset, long total) : IProgress<long>
    {
        public void Report(long value) => progress?.Report(new("正在传输安装包…", offset + value, total));
    }

    private static string QuoteShell(string value)
    {
        if (value.Contains('\0', StringComparison.Ordinal)) throw new ArgumentException("路径不能包含空字符。", nameof(value));
        return "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    }

    private static async Task<string> RunInstallShellAsync(TerminalSession terminal, string script, CancellationToken ct)
    {
        // The marker occurs only in decoded script output, never in PTY input echo.
        string token = "ANDROMELD_" + Guid.NewGuid().ToString("N");
        string wrapped = "printf '\\n" + token + ":START\\n'\n(\n" + script + "\n)\nresult=$?\nprintf '\\n" + token + ":END:%s\\n' \"$result\"\n";
        string delimiter = "AM_SCRIPT_" + Guid.NewGuid().ToString("N");
        StringBuilder command = new("base64 -d <<'" + delimiter + "' | sh\n");
        string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(wrapped));
        // PTYs can discard canonical input lines beyond a few KiB. Keep every heredoc line bounded.
        for (int i = 0; i < encoded.Length; i += 512) command.Append(encoded.AsSpan(i, Math.Min(512, encoded.Length - i))).Append('\n');
        command.Append(delimiter).Append('\n');
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        // Consume and ACK PTY echo while writing a large split-install script.
        Task<string> response = ReadInstallShellAsync(terminal, token, timeout.Token);
        try
        {
            await terminal.SendInputAsync(Encoding.UTF8.GetBytes(command.ToString()), timeout.Token).ConfigureAwait(false);
            return await response.ConfigureAwait(false);
        }
        catch
        {
            await timeout.CancelAsync().ConfigureAwait(false);
            try { await response.ConfigureAwait(false); }
            catch (Exception) { /* Preserve the input/response failure. */ }
            throw;
        }
    }

    private static async Task<string> ReadInstallShellAsync(TerminalSession terminal, string token, CancellationToken ct)
    {
        StringBuilder output = new();
        var decoder = Encoding.UTF8.GetDecoder();
        char[] chars = new char[8192];
        string startMarker = token + ":START\n", endMarker = "\n" + token + ":END:";
        bool started = false;
        while (true)
        {
            var bytes = await terminal.ReadOutputAsync(ct).ConfigureAwait(false);
            if (bytes.IsEmpty) throw new IOException("安装终端已关闭；请检查手机上的安装状态。");
            int count = decoder.GetChars(bytes.Span, chars, flush: false);
            output.Append(chars, 0, count);
            await terminal.AcknowledgeOutputAsync(bytes.Length, ct).ConfigureAwait(false);
            string text = output.ToString().Replace("\r", "", StringComparison.Ordinal);
            if (!started)
            {
                int start = text.IndexOf(startMarker, StringComparison.Ordinal);
                if (start < 0)
                {
                    if (output.Length > startMarker.Length * 2) output.Remove(0, output.Length - startMarker.Length * 2);
                    continue;
                }
                text = text[(start + startMarker.Length)..];
                output.Clear().Append(text);
                started = true;
            }
            if (output.Length > 256 * 1024) throw new IOException("安装终端输出过多。");
            int end = text.IndexOf(endMarker, StringComparison.Ordinal);
            if (end < 0) continue;
            int statusStart = end + endMarker.Length;
            int statusEnd = text.IndexOf('\n', statusStart);
            if (statusEnd < 0) continue;
            string body = text[..end].Trim();
            if (text.AsSpan(statusStart, statusEnd - statusStart).SequenceEqual("0")) return body;
            throw new IOException("Android 安装失败：" + body);
        }
    }
}
