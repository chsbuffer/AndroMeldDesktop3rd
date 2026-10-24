using System.Net;

using Andromeld.Discovery;
using Andromeld.Protocol;
using Andromeld.Testing;

namespace Andromeld.Discovery.Tests;

internal static class WirelessChecks
{
    private const string AddressText = "192.0.2.55";

    public static async Task RunAsync(Action<bool, string> check)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken ct = stop.Token;
        IPAddress address = IPAddress.Parse(AddressText);

        IReadOnlyList<AdbMdnsService> parsed = AdbMdns.Parse(
            "adb-synthetic _adb-tls-connect._tcp 192.0.2.55:41000\n" +
            "adb-synthetic\t_adb-tls-pairing._tcp.\t192.0.2.55:42000\n" +
            "adb-other _adb-tls-connect._tcp 192.0.2.56:43000\n" +
            "adb-v6 _adb-tls-connect._tcp [2001:db8::7]:44000\n" +
            "unrelated _http._tcp 192.0.2.55:80\n");
        check(parsed.Count == 4 && parsed[0].Port == 41000 && parsed[1].Type == AdbMdns.PairingService &&
              parsed[3].Address.Equals(IPAddress.Parse("2001:db8::7")),
            "ADB mDNS parsing finds tab- or space-separated TLS connect/pairing services and IPv6 endpoints");

        using var http = new HttpClient(new FakeUsbDownloads());
        string services = $"adb-synthetic _adb-tls-connect._tcp {AddressText}:41000\n" +
                          $"adb-synthetic _adb-tls-pairing._tcp {AddressText}:42000\n";
        await using (var adb = new FakeAdbServer
                     {
                         MdnsServices = services,
                         RequireWirelessPairing = true,
                         WirelessPairingCode = "135790"
                     })
        {
            var discovery = new UsbDiscovery(adb.Endpoint, httpClient: http);
            var updates = new Recorder();
            string? prompt = null;
            await using (IUsbConnection connection = await discovery.OpenWirelessAsync(address, ct, updates,
                             requestPairingCode: _ =>
                             {
                                 prompt = "asked";
                                 return Task.FromResult("135790");
                             }))
            {
                check(connection.Bootstrap.WebPort == 13019 && connection.Endpoint.IsLoopback &&
                      adb.ConnectRequests.SequenceEqual([$"{AddressText}:41000", $"{AddressText}:41000"]) &&
                      adb.PairRequests.Single() == $"135790@{AddressText}:42000" && prompt == "asked" &&
                      updates.Values.Any(static value => value.Stage == UsbConnectionStage.WaitingForWirelessPairing) &&
                      updates.Values.Any(static value => value.Stage == UsbConnectionStage.PairingWireless) &&
                      adb.Requests.Any(request => request.Contains(":forward:", StringComparison.Ordinal)),
                    "wireless ADB discovers the random connect port, pairs with the code, then reuses helper bootstrap and forward");
            }

            check(adb.ForwardCount == 0 && adb.Requests.Count(request => request.Contains(":killforward:tcp:", StringComparison.Ordinal)) == 1,
                "disposing a wireless ADB connection removes only its own forward");
        }

        await using (var fallback = new FakeAdbServer())
        {
            var discovery = new UsbDiscovery(fallback.Endpoint, httpClient: http);
            await using (await discovery.OpenWirelessAsync(address, ct))
            {
                check(fallback.ConnectRequests.Single() == $"{AddressText}:5555",
                    "wireless ADB falls back to the classic 5555 port when no mDNS connect service exists");
            }
        }

        await using (var warming = new FakeAdbServer
                     {
                         MdnsServices = $"adb-synthetic _adb-tls-connect._tcp {AddressText}:41000\n",
                         MdnsEmptyResponses = 1,
                         RejectClassicWirelessPort = true
                     })
        {
            var discovery = new UsbDiscovery(warming.Endpoint, httpClient: http);
            await using (await discovery.OpenWirelessAsync(address, ct))
            {
                check(warming.ConnectRequests.SequenceEqual([$"{AddressText}:5555", $"{AddressText}:41000"]),
                    "wireless ADB retries a warming ADB mDNS cache before declaring that no connect service exists");
            }
        }

        await using (var alreadyPaired = new FakeAdbServer
                     {
                         MdnsServices = $"adb-synthetic _adb-tls-connect._tcp {AddressText}:41000\n"
                     })
        {
            var discovery = new UsbDiscovery(alreadyPaired.Endpoint, httpClient: http);
            bool prompted = false;
            await using (await discovery.OpenWirelessAsync(address, ct, requestPairingCode: _ =>
                         {
                             prompted = true;
                             return Task.FromResult("135790");
                         }))
            {
                check(!prompted && alreadyPaired.ConnectRequests.Single() == $"{AddressText}:41000" &&
                      alreadyPaired.PairRequests.IsEmpty,
                    "an already-authenticated wireless ADB connect succeeds without requesting a pairing code");
            }
        }

        await using (var unprepared = new FakeAdbServer
                     {
                         MdnsServices = services,
                         RequireWirelessPairing = true
                     })
        {
            var discovery = new UsbDiscovery(unprepared.Endpoint, httpClient: http);
            try
            {
                await discovery.OpenWirelessAsync(address, ct);
                throw new Exception("Expected a pairing request failure");
            }
            catch (UsbConnectionException ex)
            {
                check(ex.Message.Contains("无线调试配对", StringComparison.Ordinal) && unprepared.ForwardCount == 0,
                    "wireless ADB reports that a pairing code is required and never creates a forward");
            }

            try
            {
                await discovery.OpenWirelessAsync(address, ct, requestPairingCode: _ => Task.FromResult("000000"));
                throw new Exception("Expected a wrong-code failure");
            }
            catch (UsbConnectionException ex)
            {
                check(ex.Stage == UsbConnectionStage.PairingWireless && ex.Message.Contains("配对码", StringComparison.Ordinal) &&
                      unprepared.ForwardCount == 0 && unprepared.PairRequests.Single() == $"000000@{AddressText}:42000",
                    "a rejected wireless pairing code fails with an actionable error and no forward");
            }

            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
            TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<IUsbConnection> pending = discovery.OpenWirelessAsync(address, cancel.Token,
                requestPairingCode: async token =>
                {
                    entered.SetResult();
                    await Task.Delay(Timeout.Infinite, token);
                    return "";
                });
            await entered.Task.WaitAsync(ct);
            await cancel.CancelAsync();
            try
            {
                await pending;
                throw new Exception("Expected cancellation while waiting for a pairing code");
            }
            catch (OperationCanceledException)
            {
                check(unprepared.ForwardCount == 0, "canceling while the pairing prompt is open stops wireless ADB without a forward");
            }
        }
    }

    private sealed class Recorder : IProgress<UsbConnectionProgress>
    {
        public List<UsbConnectionProgress> Values { get; } = [];
        public void Report(UsbConnectionProgress value) => Values.Add(value);
    }
}
