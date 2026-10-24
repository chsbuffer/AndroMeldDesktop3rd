using Andromeld.Protocol;

namespace Andromeld.Protocol.Tests;

public static partial class Program
{
    private static void CheckUsbBootstrap()
    {
        var bootstrap = UsbBootstrap.Parse("Result: Bundle[{ticket=39U6DY5U, webPort=13021, status=ok, responseVersion=2}]\r\n");
        Check(bootstrap is { WebPort: 13021, Ticket: "39U6DY5U" }, "USB bootstrap parses an unordered Android Bundle and dynamic port");
        foreach (var output in new[]
        {
            "Error while accessing provider",
            "Result: Bundle[{status=ok, responseVersion=1, webPort=13012, ticket=39U6DY5U}]",
            "Result: Bundle[{status=ok, responseVersion=2, webPort=12969, ticket=39U6DY5U}]",
            "Result: Bundle[{status=ok, responseVersion=2, webPort=13022, ticket=39U6DY5U}]",
            "Result: Bundle[{status=ok, responseVersion=2, webPort=13012, ticket=INVALID0}]",
            "Result: Bundle[{status=ok, responseVersion=2, webPort=13012, ticket=39U6DY5U, webPort=13013}]"
        })
        {
            try { UsbBootstrap.Parse(output); throw new Exception("Expected malformed bootstrap rejection"); }
            catch (IOException) { }
        }
        Check(true, "USB bootstrap rejects missing providers, unsupported versions, native/out-of-range ports, invalid tickets and duplicate fields");
        foreach (var code in new[] { "privacy_consent_required", "agent_unavailable", "web_service_unavailable", "unknown_method", "ticket_unavailable", "ticket_invalid", "web_version_unavailable", "future_error" })
        {
            try { UsbBootstrap.Parse($"Result: Bundle[{{status=error, errorCode={code}, ticket=39U6DY5U, code=1955-JNBT-WANX}}]"); throw new Exception("Expected typed error"); }
            catch (UsbBootstrapException ex)
            {
                Check(ex.ErrorCode == code && !ex.ToString().Contains("39U6DY5U") && !ex.ToString().Contains("1955-JNBT-WANX"), $"USB bootstrap preserves {code} without exposing ticket/code");
            }
        }
        CheckUsbHelper();
    }

    private static void CheckUsbHelper()
    {
        Check(!UsbHelper.StartAgentCommand.Contains('\r') && !UsbHelper.StopAgentCommand.Contains('\r'),
            "USB agent shell scripts use Unix line endings regardless of checkout platform");
        var hash = new string('a', 64);
        var json = $$"""{"packageName":"com.catchingnow.andfiles.helper","versionCode":98765432,"apk":"AndroMeld-WebUSB.apk","sha256":"{{hash}}"}""";
        Check(UsbHelperMetadata.Parse(json) == new UsbHelperMetadata(98765432, hash), "USB metadata takes the required helper version from official data");
        foreach (var invalid in new[] { "[]", "{", json.Replace("98765432", "0"), json.Replace("98765432", "1.5"), json.Replace("98765432", "\"98765432\""),
            json.Replace("com.catchingnow.andfiles.helper", "other.package"), json.Replace("AndroMeld-WebUSB.apk", "AndroMeld.apk"), json.Replace(hash, hash.ToUpperInvariant()) })
            ExpectUsbIOException(() => UsbHelperMetadata.Parse(invalid));
        Check(true, "USB metadata rejects wrong package/APK/hash/version and malformed JSON");
        Check(!UsbHelper.IsInstalled("\n") && UsbHelper.IsInstalled("package:/data/app/base.apk\r\npackage:/data/app/split.apk\n") &&
            UsbHelper.ParseVersion("versionCode=98765432 minSdk=26\nversionName=1.2") == 98765432 && UsbHelper.ParseVersion("versionName=missing") is null,
            "helper inspection distinguishes absent, split packages and unavailable version codes");
        ExpectUsbIOException(() => UsbHelper.IsInstalled("Error: permission denied"));
        UsbHelper.CheckInstallResult("Performing Streamed Install\nSuccess\n");
        ExpectUsbIOException(() => UsbHelper.CheckInstallResult("Failure [INSTALL_FAILED_UPDATE_INCOMPATIBLE]"));
        ExpectUsbIOException(() => UsbHelper.CheckInstallResult("NotSuccess"));
        Check(true, "helper inspection and installation reject shell/install failures");
        var pid = UsbHelper.ParseAgentPid("device_agent_pid=1234\n");
        UsbHelper.CheckAgentState("{\"pid\":1234,\"other\":true}", pid);
        foreach (var state in new[] { "{\"pid\":1235}", "{\"pid\":\"1234\"}", "[]", "{}", "not JSON" })
            ExpectUsbIOException(() => UsbHelper.CheckAgentState(state, pid));
        ExpectUsbIOException(() => UsbHelper.ParseAgentPid("agent_start=failed"));
        ExpectUsbIOException(() => UsbHelper.CheckAgentStopped("agent_restart=failed"));
        ExpectUsbIOException(() => UsbHelper.CheckActivityStarted("Error type 3"));
        Check(true, "agent preparation checks the running PID against state and reports stop/start/activity failures");
    }

    private static void ExpectUsbIOException(Action action)
    {
        try { action(); throw new Exception("Expected USB parsing error"); }
        catch (IOException) { }
    }
}
