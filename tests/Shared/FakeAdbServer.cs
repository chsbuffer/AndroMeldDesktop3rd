using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Andromeld.Protocol;

namespace Andromeld.Testing;

/// <summary>A loopback ADB server and TCP forward. Never opens a real ADB server or device.</summary>
public sealed class FakeAdbServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentBag<Task> _connections = [];
    private readonly ConcurrentDictionary<int, TcpListener> _forwards = new();
    private readonly object _devicesLock = new();
    private readonly Task _accept;
    private readonly int _targetPort;
    private int _activeStreams;
    private string _devices = "USB-ONE\tdevice usb:1-2 product:synthetic model:Test_Phone device:test transport_id:1\n";
    private int _mdnsEmptyResponses;
    private volatile bool _wirelessPaired;

    public FakeAdbServer(int targetPort = 0)
    {
        _targetPort = targetPort;
        _listener.Start();
        _accept = AcceptAsync(_listener, ServeAsync);
    }

    public IPEndPoint Endpoint => (IPEndPoint)_listener.LocalEndpoint;
    public ConcurrentQueue<string> Requests { get; } = new();
    public int ForwardCount => _forwards.Count;
    public int ActiveStreams => Volatile.Read(ref _activeStreams);
    public string Devices
    {
        get
        {
            lock (_devicesLock)
            {
                return _devices;
            }
        }
        set
        {
            lock (_devicesLock)
            {
                _devices = value;
            }
        }
    }

    public string MdnsServices { get; set; } = "";
    public int MdnsEmptyResponses
    {
        get => Volatile.Read(ref _mdnsEmptyResponses);
        set => Volatile.Write(ref _mdnsEmptyResponses, value);
    }

    public bool RequireWirelessPairing { get; set; }
    public bool RejectClassicWirelessPort { get; set; }
    public string WirelessPairingCode { get; set; } = "123456";
    public string WirelessDeviceState { get; set; } = "device";
    public ConcurrentQueue<string> ConnectRequests { get; } = new();
    public ConcurrentQueue<string> PairRequests { get; } = new();
    public string BootstrapOutput { get; set; } = "Result: Bundle[{status=ok, responseVersion=2, webPort=13019, ticket=39U6DY5U, webVersionCode=130, panelHost=app.andromeld.catchingnow.com, code=1955-JNBT-WANX}]\n";
    public bool RejectForward { get; set; }
    public string? PairingReply { get; set; }
    public TaskCompletionSource MdnsRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource PairingRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource? PairingReplyGate { get; set; }
    public TaskCompletionSource ForwardRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource? ForwardReplyGate { get; set; }
    public TaskCompletionSource? ShellReplyGate { get; set; }
    public TaskCompletionSource ShellRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ConcurrentQueue<int> StreamsAtRemoval { get; } = new();
    public bool HelperInstalled { get; set; } = true;
    public long? HelperVersion { get; set; } = 100;
    public long InstalledVersionAfterInstall { get; set; } = 100;
    public string InstallOutput { get; set; } = "Success\n";
    public string AgentStartOutput { get; set; } = "device_agent_pid=1234\n";
    public string AgentStopOutput { get; set; } = "agent_restart=stopped\n";
    public string AgentState { get; set; } = "{\"pid\":1234}";
    public string ActivityOutput { get; set; } = "Starting: Intent\n";
    public ConcurrentQueue<string> BootstrapOutputs { get; } = new();
    public ConcurrentDictionary<string, byte[]> TemporaryApks { get; } = new();
    public byte[]? InstalledApk { get; private set; }
    public bool FailCleanup { get; set; }
    public TaskCompletionSource SyncRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource? SyncReplyGate { get; set; }
    public Func<string, Task>? BeforeShellReply { get; set; }

    private async Task AcceptAsync(TcpListener listener, Func<TcpClient, Task> serve)
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(_stop.Token);
                _connections.Add(ServeSafelyAsync(client, serve));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }

    private static async Task ServeSafelyAsync(TcpClient client, Func<TcpClient, Task> serve)
    {
        using (client)
        {
            try { await serve(client); }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException) { }
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        var stream = client.GetStream();
        while (!_stop.IsCancellationRequested)
        {
            var size = new byte[4];
            await stream.ReadExactlyAsync(size, _stop.Token);
            var request = new byte[int.Parse(Encoding.ASCII.GetString(size), NumberStyles.HexNumber, CultureInfo.InvariantCulture)];
            await stream.ReadExactlyAsync(request, _stop.Token);
            var command = Encoding.UTF8.GetString(request);
            Requests.Enqueue(command);
            if (command == "host:mdns:services")
            {
                await WriteAsync(stream, "OKAY");
                bool empty = MdnsEmptyResponses > 0;
                if (empty)
                {
                    MdnsEmptyResponses--;
                }

                await WriteStringAsync(stream, empty ? "" : MdnsServices);
                MdnsRequested.TrySetResult();
                return;
            }
            if (command.StartsWith("host:connect:", StringComparison.Ordinal))
            {
                var serial = command.Substring("host:connect:".Length);
                ConnectRequests.Enqueue(serial);
                await WriteAsync(stream, "OKAY");
                if (RejectClassicWirelessPort && serial.EndsWith(":5555", StringComparison.Ordinal))
                {
                    await WriteStringAsync(stream, "failed to connect to " + serial + ": Connection refused");
                    return;
                }
                if (RequireWirelessPairing && !_wirelessPaired)
                {
                    await WriteStringAsync(stream, "failed to authenticate to " + serial);
                    return;
                }
                AddWirelessDevice(serial);
                await WriteStringAsync(stream, "connected to " + serial);
                return;
            }
            if (command.StartsWith("host:pair:", StringComparison.Ordinal))
            {
                var rest = command.Substring("host:pair:".Length);
                var separator = rest.IndexOf(':');
                var code = separator < 0 ? rest : rest[..separator];
                var serial = separator < 0 ? "" : rest[(separator + 1)..];
                PairRequests.Enqueue($"{code}@{serial}");
                PairingRequested.TrySetResult();
                if (PairingReplyGate is { } pairingGate) await pairingGate.Task.WaitAsync(_stop.Token);
                if (PairingReply is { } pairingReply)
                {
                    await WriteAsync(stream, "OKAY");
                    await WriteStringAsync(stream, pairingReply);
                    return;
                }
                if (code == WirelessPairingCode)
                {
                    _wirelessPaired = true;
                    await WriteAsync(stream, "OKAY");
                    await WriteStringAsync(stream, "Successfully paired to " + serial);
                }
                else
                {
                    await WriteAsync(stream, "FAIL");
                    await WriteStringAsync(stream, "wrong password or connection was dropped");
                }
                return;
            }
            if (command == "host:devices-l")
            {
                await WriteAsync(stream, "OKAY");
                await WriteStringAsync(stream, Devices);
                return;
            }
            if (command.StartsWith("host:transport", StringComparison.Ordinal))
            {
                await WriteAsync(stream, "OKAY");
                continue;
            }
            if (command.StartsWith("shell:", StringComparison.Ordinal))
            {
                await WriteAsync(stream, "OKAY");
                ShellRequested.TrySetResult();
                if (ShellReplyGate is { } gate) await gate.Task.WaitAsync(_stop.Token);
                var response = ShellResponse(command.Substring("shell:".Length));
                if (BeforeShellReply is { } before) await before(command).WaitAsync(_stop.Token);
                await WriteAsync(stream, response);
                return;
            }
            if (command == "sync:")
            {
                await WriteAsync(stream, "OKAY");
                await ReceiveApkAsync(stream);
                return;
            }
            if (command.Contains(":forward:", StringComparison.Ordinal))
            {
                if (RejectForward)
                {
                    await WriteAsync(stream, "FAIL");
                    await WriteStringAsync(stream, "synthetic forward failure");
                    return;
                }
                var forward = new TcpListener(IPAddress.Loopback, 0);
                forward.Start();
                var port = ((IPEndPoint)forward.LocalEndpoint).Port;
                _forwards[port] = forward;
                _connections.Add(AcceptAsync(forward, ProxyAsync));
                ForwardRequested.TrySetResult();
                if (ForwardReplyGate is { } gate) await gate.Task.WaitAsync(_stop.Token);
                await WriteAsync(stream, "OKAYOKAY");
                await WriteStringAsync(stream, port.ToString(CultureInfo.InvariantCulture));
                return;
            }
            if (command.Contains(":killforward:tcp:", StringComparison.Ordinal))
            {
                var port = int.Parse(command.AsSpan(command.LastIndexOf(':') + 1), CultureInfo.InvariantCulture);
                StreamsAtRemoval.Enqueue(ActiveStreams);
                if (_forwards.TryRemove(port, out var forward)) forward.Stop();
                await WriteAsync(stream, "OKAY");
                return;
            }
            throw new InvalidOperationException("Unexpected synthetic ADB request: " + command);
        }
    }

    private void AddWirelessDevice(string serial)
    {
        lock (_devicesLock)
        {
            if (!_devices.Contains(serial + "\t", StringComparison.Ordinal))
            {
                _devices += $"{serial}\t{WirelessDeviceState} product:synthetic model:Wireless_Phone device:test transport_id:9\n";
            }
        }
    }

    private string ShellResponse(string command)
    {
        // Android sh treats CR as part of the command, unlike Windows text readers.
        // Reject CRLF scripts before matching known commands so this fake cannot hide that wire error.
        if (command.Contains('\n') && command.Contains('\r')) return "/system/bin/sh: syntax error: unexpected ')'\n";
        if (command == UsbHelper.PackagePathCommand) return HelperInstalled ? "package:/data/app/synthetic/base.apk\n" : string.Empty;
        if (command == UsbHelper.PackageVersionCommand) return HelperVersion is { } version ? $"  versionCode={version} minSdk=26\n  versionName=test\n" : "versionName=unknown\n";
        if (command == UsbBootstrap.ShellCommand) return BootstrapOutputs.TryDequeue(out var output) ? output : BootstrapOutput;
        if (command == UsbHelper.StopAgentCommand) return AgentStopOutput;
        if (command == UsbHelper.StartAgentCommand) return AgentStartOutput;
        if (command == UsbHelper.AgentStateCommand) return AgentState;
        if (command == UsbHelper.OpenActivityCommand) return ActivityOutput;
        if (command.StartsWith("pm install -r -g ", StringComparison.Ordinal))
        {
            if (InstallOutput.Trim() == "Success")
            {
                InstalledApk = TemporaryApks[command.Substring("pm install -r -g ".Length)];
                HelperInstalled = true;
                HelperVersion = InstalledVersionAfterInstall;
            }
            return InstallOutput;
        }
        if (command.StartsWith("rm -f /data/local/tmp/AndroMeld-", StringComparison.Ordinal))
        {
            if (FailCleanup) return "rm: synthetic device unavailable\n";
            var path = command.Substring("rm -f ".Length).Split(' ')[0];
            TemporaryApks.TryRemove(path, out _);
            return "apk_cleanup=ok\n";
        }
        throw new InvalidOperationException("Unexpected synthetic shell command: " + command);
    }

    private async Task ReceiveApkAsync(NetworkStream stream)
    {
        var header = new byte[8];
        using var apk = new MemoryStream();
        string? path = null;
        while (true)
        {
            await stream.ReadExactlyAsync(header, _stop.Token);
            var command = Encoding.ASCII.GetString(header, 0, 4);
            var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
            if (command == "DONE")
            {
                if (path is null) throw new InvalidOperationException("Missing SEND");
                TemporaryApks[path] = apk.ToArray();
                SyncRequested.TrySetResult();
                if (SyncReplyGate is { } gate) await gate.Task.WaitAsync(_stop.Token);
                await stream.WriteAsync(new byte[] { 79, 75, 65, 89, 0, 0, 0, 0 }, _stop.Token);
                return;
            }
            if (command == "QUIT") return;
            var bytes = new byte[length];
            await stream.ReadExactlyAsync(bytes, _stop.Token);
            if (command == "SEND")
            {
                path = Encoding.UTF8.GetString(bytes).Split(',')[0];
                TemporaryApks[path] = [];
            }
            else if (command == "DATA") await apk.WriteAsync(bytes, _stop.Token);
            else throw new InvalidOperationException("Unexpected sync command: " + command);
        }
    }

    private async Task ProxyAsync(TcpClient source)
    {
        using var target = new TcpClient();
        await target.ConnectAsync(IPAddress.Loopback, _targetPort, _stop.Token);
        Interlocked.Increment(ref _activeStreams);
        try
        {
            using var copy = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            var upstream = source.GetStream().CopyToAsync(target.GetStream(), copy.Token);
            var downstream = target.GetStream().CopyToAsync(source.GetStream(), copy.Token);
            await Task.WhenAny(upstream, downstream);
            await copy.CancelAsync();
            try { await Task.WhenAll(upstream, downstream); }
            catch (OperationCanceledException) { }
        }
        finally { Interlocked.Decrement(ref _activeStreams); }
    }

    private Task WriteAsync(NetworkStream stream, string text) => stream.WriteAsync(Encoding.UTF8.GetBytes(text), _stop.Token).AsTask();
    private async Task WriteStringAsync(NetworkStream stream, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await WriteAsync(stream, bytes.Length.ToString("X4", CultureInfo.InvariantCulture));
        await stream.WriteAsync(bytes, _stop.Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        foreach (var forward in _forwards.Values) forward.Stop();
        await _accept;
        await Task.WhenAll(_connections);
        _stop.Dispose();
    }
}
