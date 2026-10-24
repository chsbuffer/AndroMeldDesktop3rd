using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Andromeld.Testing;

public sealed partial class MockPhone
{
    private sealed class Terminals
    {
        public int NextId;
        public ConcurrentDictionary<int, byte> Active { get; } = new();
    }
    private readonly ConditionalWeakTable<WebSocket, Terminals> _terminals = new();
    private readonly ConcurrentDictionary<int, WebSocket> _terminalSockets = new();
    private sealed class TerminalFlow { public long Sent; public long Acked; }
    private readonly ConcurrentDictionary<int, TerminalFlow> _terminalFlow = new();
    public ConcurrentQueue<JsonElement> TerminalCommands { get; } = new();
    public ConcurrentQueue<(int Id, byte[] Bytes)> TerminalInputs { get; } = new();
    public TaskCompletionSource? HoldTerminalOpen { get; set; }
    public bool SupportsTerminal { get; set; } = true;
    public bool SimulatePackageInstaller { get; set; }
    public Func<string, Task<string>>? InstallationShellHandler { get; set; }
    private readonly Dictionary<int, StringBuilder> _installationInput = new();
    public const string TerminalBanner = "\u001b[32mAndroid 终端 ✓\u001b[0m\r\n/system/bin/sh\r\n$ ";

    private async Task HandleTerminalAsync(WebSocket socket, JsonElement request)
    {
        TerminalCommands.Enqueue(request.Clone());
        var terminals = _terminals.GetOrCreateValue(socket);
        switch (request.GetProperty("c").GetString())
        {
            case "term-open":
                var reqId = request.GetProperty("reqId").GetString();
                var cols = request.GetProperty("cols").GetInt32();
                var rows = request.GetProperty("rows").GetInt32();
                if (cols is < 1 or > 500 || rows is < 1 or > 300 || reqId?.Length is < 1 or > 64)
                    throw new IOException("Invalid terminal open");
                if (terminals.Active.Count == 4)
                {
                    await SendAsync(socket, new { c = "term-opened", reqId, err = "limit" });
                    return;
                }
                if (HoldTerminalOpen is { } hold) await hold.Task;
                var id = ++terminals.NextId;
                terminals.Active[id] = 0;
                _terminalSockets[id] = socket;
                _terminalFlow[id] = new();
                await SendAsync(socket, new { c = "term-opened", reqId, id });
                var banner = Encoding.UTF8.GetBytes(TerminalBanner);
                // Deliberately split the UTF-8 encoding of 中/终 across separate protocol frames.
                await PushTerminalBytesAsync(socket, id, banner[..14]);
                await PushTerminalBytesAsync(socket, id, banner[14..]);
                break;
            case "term-ack":
                Interlocked.Exchange(ref _terminalFlow[request.GetProperty("id").GetInt32()].Acked, request.GetProperty("received").GetInt64());
                break;
            case "term-close":
                terminals.Active.TryRemove(request.GetProperty("id").GetInt32(), out _);
                break;
        }
    }

    private async Task HandleTerminalInputAsync(WebSocket socket, byte[] frame)
    {
        var id = BinaryPrimitives.ReadInt32BigEndian(frame.AsSpan(2));
        var bytes = frame[6..];
        TerminalInputs.Enqueue((id, bytes));
        if (SimulatePackageInstaller || InstallationShellHandler is not null)
        {
            if (!_installationInput.TryGetValue(id, out var input)) _installationInput[id] = input = new();
            input.Append(Encoding.UTF8.GetString(bytes));
            string command = input.ToString();
            int start = command.IndexOf('\'') + 1, end = command.IndexOf('\'', start);
            string delimiter = end >= start && start > 0 ? command[start..end] : "";
            if (delimiter.Length > 0 && command.EndsWith("\n" + delimiter + "\n", StringComparison.Ordinal))
            {
                input.Clear();
                int body = command.IndexOf('\n') + 1;
                string script = Encoding.UTF8.GetString(Convert.FromBase64String(command[body..^(delimiter.Length + 1)]));
                string output;
                if (InstallationShellHandler is { } handler) output = await handler(script);
                else
                {
                    var marker = System.Text.RegularExpressions.Regex.Match(script, "ANDROMELD_[a-f0-9]+").Value;
                    output = "\n" + marker + ":START\n" + (script.Contains("getprop", StringComparison.Ordinal) ? "36\narm64-v8a,armeabi-v7a" : "Success") + "\n" + marker + ":END:0\n";
                }
                await PushTerminalBytesAsync(socket, id, Encoding.UTF8.GetBytes(output));
            }
            return;
        }
        if (_terminals.GetOrCreateValue(socket).Active.ContainsKey(id)) await PushTerminalBytesAsync(socket, id, bytes);
    }

    private async Task PushTerminalBytesAsync(WebSocket socket, int id, ReadOnlyMemory<byte> bytes)
    {
        var flow = _terminalFlow[id];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (Interlocked.Read(ref flow.Sent) - Interlocked.Read(ref flow.Acked) >= 131072) await Task.Delay(1, timeout.Token);
        var frame = new byte[6 + bytes.Length]; frame[0] = 6; frame[1] = 1;
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(2), id); bytes.CopyTo(frame.AsMemory(6));
        Interlocked.Add(ref flow.Sent, bytes.Length);
        await SendBytesAsync(socket, frame, false);
    }
    public async Task PushTerminalOutputAsync(int id, byte[] bytes, int chunkSize = 8192)
    {
        var socket = _terminalSockets[id];
        foreach (var chunk in bytes.Chunk(chunkSize)) await PushTerminalBytesAsync(socket, id, chunk);
    }
    public async Task ExitTerminalAsync(int id, int code)
    {
        var socket = _terminalSockets[id];
        _terminals.GetOrCreateValue(socket).Active.TryRemove(id, out _);
        await SendAsync(socket, new { c = "term-exit", id, code });
    }
}
