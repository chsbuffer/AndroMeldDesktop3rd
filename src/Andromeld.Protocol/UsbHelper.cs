using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Andromeld.Protocol;

/// <summary>Shell commands and parsers verified against the official Web USB setup flow.</summary>
public static partial class UsbHelper
{
    public const string PackageName = "com.catchingnow.andfiles.helper";
    public const string PackagePathCommand = "pm path " + PackageName;
    public const string PackageVersionCommand = "dumpsys package " + PackageName;
    public const string AgentStateCommand = "cat /data/local/tmp/AndroMeldAgent/state.json";
    public const string OpenActivityCommand = "am start -n " + PackageName + "/.AndroMeldMainActivity";

    public static string StopAgentCommand { get; } = LoadAgentScript("StopAgent.sh");

    public static string StartAgentCommand { get; } = LoadAgentScript("StartAgent.sh");

    public static string TemporaryApkPath(Guid id)
    {
        return $"/data/local/tmp/AndroMeld-{id:N}.apk";
    }

    public static string InstallCommand(Guid id)
    {
        return "pm install -r -g " + TemporaryApkPath(id);
    }

    public static string CleanupCommand(Guid id)
    {
        return "rm -f " + TemporaryApkPath(id) + " && echo apk_cleanup=ok";
    }

    private static string LoadAgentScript(string name)
    {
        using Stream stream = typeof(UsbHelper).Assembly.GetManifestResourceStream("Andromeld.Protocol.Usb." + name)
                              ?? throw new InvalidOperationException("Missing embedded USB agent script: " + name);
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }

    public static bool IsInstalled(string paths)
    {
        string text = paths.Trim();
        if (text.Length == 0)
        {
            return false;
        }

        if (!text.Split('\n').All(static line => PackagePathPattern().IsMatch(line.Trim())))
        {
            throw new IOException("无法检查手机 helper 安装路径，请检查 ADB 调试权限后重试。");
        }

        return true;
    }

    public static long? ParseVersion(string output)
    {
        return VersionPattern().Match(output) is { Success: true } match &&
               long.TryParse(match.Groups["version"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long version) && version > 0
            ? version
            : null;
    }

    public static void CheckInstallResult(string output)
    {
        if (!output.Split('\n').Any(static line => line.Trim() is "Success"))
        {
            throw new IOException("手机 helper 安装失败，请检查可用空间、安装权限及签名冲突后重试。");
        }
    }

    public static void CheckAgentStopped(string output)
    {
        if (!output.Split('\n').Any(static line => line.Trim() is "agent_restart=stopped"))
        {
            throw new IOException("无法停止旧 AndroMeld 服务，请在手机检查服务后重试。");
        }
    }

    public static int ParseAgentPid(string output)
    {
        Match match = AgentPidPattern().Match(output);
        if (!match.Success || !int.TryParse(match.Groups["pid"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int pid) || pid <= 0)
        {
            throw new IOException("手机 AndroMeld 服务启动失败，请在手机检查服务后重试。");
        }

        return pid;
    }

    public static void CheckAgentState(string json, int runningPid)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("pid", out JsonElement pid) &&
                pid.ValueKind == JsonValueKind.Number && pid.TryGetInt32(out int value) && value == runningPid && value > 0)
            {
                return;
            }
        }
        catch (JsonException)
        {
            /* The state may contain private service information. */
        }

        throw new IOException("手机 AndroMeld 服务状态与运行进程不一致，请重试。");
    }

    public static void CheckActivityStarted(string output)
    {
        if (ShellErrorPattern().IsMatch(output))
        {
            throw new IOException("无法打开手机 AndroMeld，请手动打开应用后重试。");
        }
    }

    [GeneratedRegex(@"^package:\S+$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex PackagePathPattern();

    [GeneratedRegex(@"\bversionCode=(?<version>\d+)\b", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"(?m)^device_agent_pid=(?<pid>\d+)\r?$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex AgentPidPattern();

    [GeneratedRegex(@"\bError\b", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex ShellErrorPattern();
}