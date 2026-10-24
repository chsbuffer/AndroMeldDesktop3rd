using Andromeld.Protocol;
using Andromeld.Testing;

namespace Andromeld.Protocol.Tests;

public static partial class Program
{
    private static async Task CheckDeviceInformationAsync(AndromeldClient client, MockPhone server)
    {
        var information = await client.GetDeviceInformationAsync(CancellationToken.None);
        Check(information is { Id: "mock", Android: "16", Sdk: 36, Battery: { Level: 87, Charging: false },
            Storage: { Total: 114_700_000_000L, Free: 7_300_000_000L } },
            "device-info uses the same command for request/reply and preserves 64-bit byte counts");
        server.DeviceInformationOverride = new { id = "mock", name = "模拟手机", battery = new { level = -2147483648, charging = true } };
        information = await client.GetDeviceInformationAsync(CancellationToken.None);
        Check(information.Storage is null && information.Android is null && information.Battery is { Level: int.MinValue, Charging: true },
            "device-info permits unavailable storage/version and preserves Android's unknown battery sentinel");
        server.DeviceInformationError = "unavailable";
        try { await client.GetDeviceInformationAsync(CancellationToken.None); throw new Exception("Expected device information error"); }
        catch (IOException ex) { Check(ex.Message.Contains("unavailable"), "device-info errors do not invalidate the main connection"); }
        server.DeviceInformationError = null;
        server.DeviceInformationOverride = null;
    }
}
