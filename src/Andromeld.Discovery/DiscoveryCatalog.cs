using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Andromeld.Discovery;

// One announcement per interface/instance; removing one interface must not remove the other routes.
internal sealed class DiscoveryCatalog
{
    private readonly Dictionary<(string Interface, string Instance), LanDevice> _announcements = [];

    public void Update(string type, string instance, int port, IEnumerable<string> txt,
        IEnumerable<IPAddress> addresses, string interfaceId, string interfaceName)
    {
        (string interfaceId, string) key = (interfaceId, instance.ToLowerInvariant());
        _announcements.Remove(key);
        if (!string.Equals(type.TrimEnd('.'), LanDiscovery.ServiceType, StringComparison.OrdinalIgnoreCase)
            || port is < 1 or > 65535)
        {
            return;
        }

        Dictionary<string, string> fields = new(StringComparer.OrdinalIgnoreCase);
        foreach (string item in txt)
        {
            int split = item.IndexOf('=');
            if (split > 0)
            {
                fields[item[..split]] = item[(split + 1)..];
            }
        }

        if (!fields.TryGetValue("v", out string? version) || version is not "1"
                                                          || !fields.TryGetValue("id", out string? id) || id.Length != 16 || !id.All(Uri.IsHexDigit))
        {
            return;
        }

        if (fields.TryGetValue("p", out string? advertisedPort)
            && (!int.TryParse(advertisedPort, NumberStyles.None, CultureInfo.InvariantCulture, out int parsedPort) || parsedPort != port))
        {
            return;
        }

        LanEndpoint[] endpoints = addresses.Where(static ip => ip.AddressFamily == AddressFamily.InterNetwork
                                                               && !IPAddress.IsLoopback(ip) && !ip.Equals(IPAddress.Any) &&
                                                               ip.GetAddressBytes()[0] < 224)
            .Distinct().Select(ip => new LanEndpoint(ip, port, interfaceId, interfaceName)).ToArray();
        if (endpoints.Length == 0)
        {
            return;
        }

        string? name = fields.GetValueOrDefault("n");
        _announcements[key] = new LanDevice(id.ToLowerInvariant(), instance.ToLowerInvariant(),
            string.IsNullOrWhiteSpace(name) ? instance : name, endpoints);
    }

    public void Remove(string interfaceId, string instance)
    {
        _announcements.Remove((interfaceId, instance.ToLowerInvariant()));
    }

    public void Clear()
    {
        _announcements.Clear();
    }

    public LanDevice[] Snapshot()
    {
        return _announcements.Values.GroupBy(static d => d.Key, StringComparer.Ordinal)
            .Select(static group => group.First() with { Endpoints = group.SelectMany(static d => d.Endpoints).Distinct().ToArray() })
            .OrderBy(static d => d.Name, StringComparer.Ordinal).ThenBy(static d => d.Key, StringComparer.Ordinal).ToArray();
    }
}