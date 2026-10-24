using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;

using Andromeld.Protocol;

namespace Andromeld.Discovery;

public static class WebEndpointResolver
{
    public static async Task<Uri?> FindAsync(LanDevice device, Uri? preferredEndpoint, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        IPAddress[] addresses = device.Endpoints.Select(static ep => ep.Address).Distinct().ToArray();
        // A saved Web port is useful after a server restart or for a configured nonstandard port.
        if (preferredEndpoint is { Scheme: "ws" } && Array.Exists(addresses,
                                                      ip => string.Equals(ip.ToString(), preferredEndpoint.DnsSafeHost, StringComparison.Ordinal))
                                                  && await ProbeAsync(preferredEndpoint, ct).ConfigureAwait(false))
        {
            return preferredEndpoint;
        }

        return await ScanAsync(addresses.Select(static ip => ip.ToString()), ct).ConfigureAwait(false);
    }

    public static async Task<Uri?> FindAsync(string host, Uri? preferredEndpoint, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (preferredEndpoint is { Scheme: "ws" } &&
            string.Equals(host, preferredEndpoint.DnsSafeHost, StringComparison.OrdinalIgnoreCase) &&
            await ProbeAsync(preferredEndpoint, ct).ConfigureAwait(false))
        {
            return preferredEndpoint;
        }

        return await ScanAsync([host], ct).ConfigureAwait(false);
    }

    private static async Task<Uri?> ScanAsync(IEnumerable<string> hosts, CancellationToken ct)
    {
        using CancellationTokenSource scan = CancellationTokenSource.CreateLinkedTokenSource(ct);
        IEnumerable<Uri> candidates = hosts.SelectMany(host => Enumerable.Range(AndromeldEndpoint.DefaultWebPort, AndromeldEndpoint.WebPortCount)
            .Select(port => new UriBuilder("ws", host, port, AndromeldEndpoint.ControlPath).Uri));
        List<Task<Uri?>> pending = candidates.Select(async endpoint =>
            await ProbeAsync(endpoint, scan.Token).ConfigureAwait(false) ? endpoint : null).ToList();
        try
        {
            while (pending.Count > 0)
            {
                Task<Uri?> done = await Task.WhenAny(pending).ConfigureAwait(false);
                pending.Remove(done);
                ct.ThrowIfCancellationRequested();
                if (await done.ConfigureAwait(false) is { } endpoint)
                {
                    return endpoint;
                }
            }

            ct.ThrowIfCancellationRequested();
            return null;
        }
        finally
        {
            await scan.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
    }

    internal static async Task<bool> ProbeAsync(Uri endpoint, CancellationToken ct)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(1500));
        using ClientWebSocket socket = new();
        socket.Options.Proxy = null;
        socket.Options.SetRequestHeader(nameof(AndromeldClient.Origin), AndromeldClient.Origin);
        try
        {
            // Only perform the HTTP upgrade. Discovery never sends pairing codes, tokens or auth.
            await socket.ConnectAsync(endpoint, timeout.Token).ConfigureAwait(false);
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Discovery", timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is WebSocketException or HttpRequestException or SocketException or OperationCanceledException)
        {
            return false;
        }
    }
}
