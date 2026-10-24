using System.Diagnostics.CodeAnalysis;
using System.Net;

namespace Andromeld.Protocol;

/// <summary>AndroMeld LAN control endpoint rules, independent of discovery and UI state.</summary>
public static class AndromeldEndpoint
{
    public const int DefaultWebPort = 13012;
    public const int WebPortCount = 10;
    public const string ControlPath = "/ws";

    public static Uri Parse(string address)
    {
        return TryParse(address, out Uri? endpoint)
            ? endpoint
            : throw new FormatException("请输入有效的 IP 地址，例如 192.168.1.7。");
    }

    /// <summary>Parses a host or legacy host:port value into the Web control endpoint.</summary>
    public static bool TryParse(string? address, [NotNullWhen(true)] out Uri? endpoint)
    {
        endpoint = null;
        if (!TryParseParts(address, out string? host, out int? port, out string scheme))
        {
            return false;
        }

        endpoint = new UriBuilder(scheme, host, port ?? DefaultWebPort, ControlPath).Uri;
        return true;
    }

    /// <summary>Separates the user-entered IP/host from an optional legacy port; the UI no longer requires one.</summary>
    public static bool TryParseAddress(string? address, [NotNullWhen(true)] out string? host, out int? port)
    {
        if (!TryParseParts(address, out host, out port, out string scheme) || scheme is not "ws")
        {
            host = null;
            port = null;
            return false;
        }

        return true;
    }

    /// <summary>Builds the plaintext control endpoint for a host the caller already validated.</summary>
    public static Uri CreateEndpoint(string host, int port)
    {
        return new UriBuilder("ws", host, port, ControlPath).Uri;
    }

    private static bool TryParseParts(string? address, [NotNullWhen(true)] out string? host, out int? port, out string scheme)
    {
        host = null;
        port = null;
        scheme = "ws";
        if (string.IsNullOrWhiteSpace(address))
        {
            return false;
        }

        string input = address.Trim();
        // Bare IPv6 literals contain colons, so a URI parser would mistake the last group for a port.
        // Bracketed literals go through the URI parser so an optional trailing port is retained.
        if (!input.Contains("://", StringComparison.Ordinal) && !input.StartsWith('[') &&
            IPAddress.TryParse(input, out IPAddress? literal))
        {
            host = literal.ToString();
            return true;
        }

        string candidate = input.Contains("://", StringComparison.Ordinal) ? input : $"ws://{input}";
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("ws" or "wss") ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            uri.Host.Length == 0 || uri.Port is < 1 or > 65535)
        {
            return false;
        }

        string name = uri.DnsSafeHost;
        if (IPAddress.TryParse(name, out IPAddress? addressValue))
        {
            host = addressValue.ToString();
        }
        else if (Uri.CheckHostName(name) is UriHostNameType.Dns)
        {
            host = name;
        }
        else
        {
            return false;
        }

        scheme = uri.Scheme;
        port = uri.IsDefaultPort ? null : uri.Port;
        return true;
    }
}
