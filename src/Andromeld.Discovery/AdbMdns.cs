using System.Globalization;
using System.Net;
using System.Net.Sockets;

using AdvancedSharpAdbClient;

namespace Andromeld.Discovery;

/// <summary>
///     Queries the ADB server's own mDNS cache. Android 11+ wireless debugging advertises the random
///     connect and pairing ports through these services; the ports change whenever wireless debugging restarts.
/// </summary>
internal static class AdbMdns
{
    public const string ConnectService = "_adb-tls-connect._tcp";
    public const string PairingService = "_adb-tls-pairing._tcp";

    public static async Task<IReadOnlyList<AdbMdnsService>> QueryAsync(EndPoint serverEndpoint, CancellationToken ct)
    {
        using TcpSocket tcp = new();
        await tcp.ConnectAsync(serverEndpoint, ct).ConfigureAwait(false);
        using AdbSocket socket = new(tcp);
        await socket.SendAdbRequestAsync("host:mdns:services", ct).ConfigureAwait(false);
        await socket.ReadAdbResponseAsync(ct).ConfigureAwait(false);
        return Parse(await socket.ReadStringAsync(ct).ConfigureAwait(false));
    }

    internal static IReadOnlyList<AdbMdnsService> Parse(string output)
    {
        List<AdbMdnsService> services = [];
        foreach (string raw in output.Split('\n'))
        {
            string[] fields = raw.Split('\t', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (fields.Length < 3)
            {
                fields = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }

            if (fields.Length < 3)
            {
                continue;
            }

            string type = fields[1].TrimEnd('.');
            if (type is not (ConnectService or PairingService) ||
                !TryParseEndpoint(fields[2], out IPAddress? address, out int port))
            {
                continue;
            }

            services.Add(new AdbMdnsService(fields[0], type, address, port));
        }

        return services;
    }

    private static bool TryParseEndpoint(string text, out IPAddress address, out int port)
    {
        text = text.Trim();
        if (IPEndPoint.TryParse(text, out IPEndPoint? endpoint) && endpoint.Port is > 0 and <= 65535)
        {
            address = endpoint.Address;
            port = endpoint.Port;
            return true;
        }

        int separator = text.LastIndexOf(':');
        if (separator > 0 &&
            int.TryParse(text.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out port) &&
            port is > 0 and <= 65535 &&
            IPAddress.TryParse(text.AsSpan(0, separator).Trim('[').Trim(']'), out IPAddress? parsed))
        {
            address = parsed;
            return true;
        }

        address = IPAddress.None;
        port = 0;
        return false;
    }
}
