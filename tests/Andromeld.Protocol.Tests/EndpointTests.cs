using Andromeld.Protocol;

namespace Andromeld.Protocol.Tests;

public static partial class Program
{
    private static void CheckEndpoints()
    {
        Check(AndromeldEndpoint.Parse(" 192.0.2.7:13018 ").AbsoluteUri == "ws://192.0.2.7:13018/ws",
            "protocol endpoint normalizes a legacy LAN address without losing its Web port");
        Check(AndromeldEndpoint.Parse("192.0.2.7").AbsoluteUri == "ws://192.0.2.7:13012/ws",
            "protocol endpoint accepts an IP address without a port and applies the default Web port");
        Check(AndromeldEndpoint.Parse("android.local").AbsoluteUri == "ws://android.local:13012/ws" &&
            AndromeldEndpoint.Parse("wss://android.local").AbsoluteUri == "wss://android.local:13012/ws",
            "protocol endpoint owns the default Web port and control path for ws/wss");
        Check(AndromeldEndpoint.Parse("ws://[2001:db8::7]:13019/old").AbsoluteUri == "ws://[2001:db8::7]:13019/ws",
            "protocol endpoint handles IPv6 and selects the main control path");

        Check(AndromeldEndpoint.TryParseAddress(" 192.0.2.7 ", out string? host, out int? port) && host == "192.0.2.7" && port is null,
            "address parsing separates a bare IP from the optional port");
        Check(AndromeldEndpoint.TryParseAddress("192.0.2.7:13018", out host, out port) && host == "192.0.2.7" && port == 13018,
            "address parsing retains an explicitly typed legacy port");
        Check(AndromeldEndpoint.TryParseAddress("2001:db8::7", out host, out port) && host == "2001:db8::7" && port is null &&
            AndromeldEndpoint.TryParseAddress("[2001:db8::7]:13018", out host, out port) && host == "2001:db8::7" && port == 13018,
            "address parsing handles bare and bracketed IPv6 literals without treating a group as a port");
        Check(AndromeldEndpoint.CreateEndpoint("2001:db8::7", 13018).AbsoluteUri == "ws://[2001:db8::7]:13018/ws",
            "protocol builds a control endpoint from a validated IPv6 host");
        Check(!AndromeldEndpoint.TryParseAddress("wss://192.0.2.7", out _, out _) &&
            AndromeldEndpoint.TryParseAddress("ws://192.0.2.7", out host, out port) && host == "192.0.2.7" && port is null,
            "IP address input rejects a secure scheme instead of silently downgrading, while explicit ws is accepted");
        Check(!AndromeldEndpoint.TryParse(null, out _) && !AndromeldEndpoint.TryParse(" ", out _) &&
            !AndromeldEndpoint.TryParseAddress(null, out _, out _) && !AndromeldEndpoint.TryParseAddress(" ", out _, out _),
            "optional endpoint parsing rejects missing input without throwing");
        foreach (string invalid in new[]
                 {
                     "http://192.0.2.7:13012", "ws://user:secret@192.0.2.7:13012", "ws://192.0.2.7:13012/?token=x",
                     "ws://192.0.2.7:13012/#code", "192.0.2.7:99999", "192.0.2.7:0", "not a host"
                 })
        {
            Check(!AndromeldEndpoint.TryParse(invalid, out Uri? endpoint) && endpoint is null &&
                !AndromeldEndpoint.TryParseAddress(invalid, out _, out _),
                "protocol endpoint rejects unsupported scheme, user-info, query, fragment or invalid port");
            try { AndromeldEndpoint.Parse(invalid); throw new Exception("Invalid endpoint accepted"); }
            catch (FormatException) { }
        }
    }
}
