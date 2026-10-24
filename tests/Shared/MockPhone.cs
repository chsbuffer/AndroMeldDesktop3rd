using System.Net;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Buffers.Binary;
using System.Text.Json;
using Andromeld.Protocol;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace Andromeld.Testing;

public sealed partial class MockPhone : IAsyncDisposable
{
    public static readonly byte[] Payload = Enumerable.Range(0, 600000).Select(i => (byte)(i % 251)).ToArray();
    public const string Png = "iVBORw0KGgoAAAANSUhEUgAAAEAAAABACAYAAACqaXHeAAAAmElEQVR4nO3QMREAIBDAsBeLCBRjA2RkoEP2Xmftc382OkBrgA7QGqADtAboAK0BOkBrgA7QGqADtAboAK0BOkBrgA7QGqADtAboAK0BOkBrgA7QGqADtAboAK0BOkBrgA7QGqADtAboAK0BOkBrgA7QGqADtAboAK0BOkBrgA7QGqADtAboAK0BOkBrgA7QGqADtAboAO0BPK0SlfKAZJoAAAAASUVORK5CYII=";
    private readonly WebApplication _app;
    private readonly string _deviceId;
    private static readonly ConditionalWeakTable<WebSocket, SemaphoreSlim> Sends = new();
    private WebSocket? _clipboardSocket;
    public const string RemoteClipboard = "手机剪贴板\nUnicode ✓";
    public System.Collections.Concurrent.ConcurrentQueue<(byte Type, JsonElement Message)> ClipboardMessages { get; } = new();
    public Task PushClipboardAsync(string text) => SendAsync(_clipboardSocket ?? throw new InvalidOperationException("Clipboard is disabled"), new { text }, channel: 3);
    public Uri Endpoint { get; }
    public List<int> IconBatches { get; } = [];
    public List<long> Acks { get; } = [];
    public ConcurrentDictionary<string, FileEntry[]> FileListings { get; } = new(StringComparer.Ordinal);
    public ConcurrentQueue<string> FileListRequests { get; } = new();
    public ConcurrentQueue<string> DownloadPaths { get; } = new();
    public ConcurrentQueue<string[]> DeleteRequests { get; } = new();
    public string? FileDeleteError { get; set; }
    public ConcurrentQueue<(string Path, string Name)> CreateDirectoryRequests { get; } = new();
    public string? FileMkdirError { get; set; }
    public string FileMkdirReplyOperation { get; set; } = "mkdir";
    public TaskCompletionSource? FileMkdirReplyGate { get; set; }
    public object? DeviceInformationOverride { get; set; }
    public string? DeviceInformationError { get; set; }
    public TaskCompletionSource? DeviceInformationReplyGate { get; set; }
    private int _deviceInformationRequests;
    public int DeviceInformationRequests => Volatile.Read(ref _deviceInformationRequests);
    public System.Collections.Concurrent.ConcurrentQueue<string> AuthenticationTypes { get; } = new();
    public System.Collections.Concurrent.ConcurrentQueue<string> BootstrapTickets { get; } = new();
    public string? ResumeDenialReason { get; set; }
    public string? PairDenialReason { get; set; }
    public TaskCompletionSource? PairingReplyGate { get; set; }
    public bool SawStringCursor;
    public TaskCompletionSource FinalAck { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private MockPhone(WebApplication app, Uri endpoint, string deviceId) { _app = app; Endpoint = endpoint; _deviceId = deviceId; }
    public static async Task<MockPhone> StartAsync(string deviceId = "mock")
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        app.UseWebSockets();
        MockPhone? phone = null;
        app.Map("/ws", async context =>
        {
            if (context.Request.Headers.Origin != AndromeldClient.Origin) { context.Response.StatusCode = 403; return; }
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            try { await phone!.ServeAsync(socket); }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException) { }
        });
        app.Map("/media", async context =>
        {
            if (context.Request.Headers.Origin != AndromeldClient.Origin) { context.Response.StatusCode = 403; return; }
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            try { await phone!.ServeMediaAsync(socket); }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException) { }
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        phone = new MockPhone(app, new Uri(address.Replace("http://", "ws://") + "/ws"), deviceId);
        return phone;
    }
    private async Task ServeAsync(WebSocket socket)
    {
        var (kind, authBytes) = await ReadAsync(socket);
        if (kind != WebSocketMessageType.Text) throw new Exception("Auth must be text");
        using var auth = JsonDocument.Parse(authBytes);
        AuthenticationTypes.Enqueue(auth.RootElement.GetProperty("t").GetString()!);
        if (auth.RootElement.GetProperty("t").GetString() == "bootstrap")
        {
            var ticket = auth.RootElement.GetProperty("ticket").GetString()!;
            BootstrapTickets.Enqueue(ticket);
            if (ticket != "39U6DY5U") { await SendAsync(socket, new { t = "deny", reason = "UNAUTHORIZED" }, true); return; }
        }
        var resume = auth.RootElement.GetProperty("t").GetString() == "resume";
        var denial = resume ? ResumeDenialReason ?? (auth.RootElement.GetProperty("token").GetString() != "test-token" ? "UNAUTHORIZED" : null) : PairDenialReason;
        if (denial is not null)
        { await SendAsync(socket, new { t = "deny", reason = denial }, true); return; }
        if (!resume && PairingReplyGate is { } pairingGate)
        {
            await SendAsync(socket, new { t = "pair-code", code = "123456" }, true);
            await pairingGate.Task.WaitAsync(_app.Lifetime.ApplicationStopping);
        }
        var welcome = new Dictionary<string, object> { ["t"] = "welcome", ["proto"] = 2, ["device"] = new { name = "模拟手机", id = _deviceId }, ["caps"] = new[] { "app-list", "fs", "file", "video", "multi-session", "phone-screen", "clipboard" } };
        if (SupportsTerminal) welcome["caps"] = ((string[])welcome["caps"]).Append("terminal").ToArray();
        if (SupportsAudio) welcome["caps"] = ((string[])welcome["caps"]).Concat(["audio", "device-audio"]).ToArray();
        if (SupportsNotifications) welcome["caps"] = ((string[])welcome["caps"]).Append("notification").ToArray();
        if (!resume) welcome["token"] = "test-token";
        await SendAsync(socket, welcome, true);
        while (socket.State == WebSocketState.Open)
        {
            var (_, bytes) = await ReadAsync(socket);
            if (bytes.Length >= 2 && bytes[0] == 2)
            {
                await HandleAudioSubscriptionAsync(socket, bytes);
                continue;
            }
            if (bytes.Length >= 2 && bytes[0] == 5 && bytes[1] <= 4)
            {
                await HandleUploadAsync(socket, bytes);
                continue;
            }
            if (bytes.Length >= 6 && bytes[0] == 6 && bytes[1] == 1)
            {
                await HandleTerminalInputAsync(socket, bytes);
                continue;
            }
            if (bytes.Length >= 2 && bytes[0] == 3)
            {
                using var clipboard = JsonDocument.Parse(bytes.AsMemory(2));
                ClipboardMessages.Enqueue((bytes[1], clipboard.RootElement.Clone()));
                if (bytes[1] == 3)
                {
                    _clipboardSocket = clipboard.RootElement.GetProperty("on").GetBoolean() ? socket : null;
                    if (_clipboardSocket is not null) await PushClipboardAsync(RemoteClipboard);
                }
                else if (bytes[1] == 2) await SendAsync(socket, new { text = clipboard.RootElement.GetProperty("text").GetString() }, channel: 3);
                else throw new Exception("Invalid clipboard type");
                continue;
            }
            if (bytes.Length < 2 || bytes[0] != 4 || bytes[1] != 1) continue;
            using var doc = JsonDocument.Parse(bytes.AsMemory(2));
            var request = doc.RootElement;
            var c = request.GetProperty("c").GetString();
            var reqId = request.TryGetProperty("reqId", out var r) ? r.GetString() : null;
            if (c is "notif-subscribe" or "notif-cmd")
            {
                await HandleNotificationsAsync(socket, request);
                continue;
            }
            if (c?.StartsWith("term-", StringComparison.Ordinal) == true)
            {
                await HandleTerminalAsync(socket, request);
                continue;
            }
            switch (c)
            {
                case "device-info":
                    if (request.EnumerateObject().Count() != 1) throw new IOException("device-info has no additional fields");
                    Interlocked.Increment(ref _deviceInformationRequests);
                    var information = DeviceInformationOverride ?? new DeviceInformation(_deviceId, "模拟手机", "16", 36,
                        new(87, false), new(114_700_000_000L, 7_300_000_000L));
                    if (DeviceInformationReplyGate is { } gate) await gate.Task;
                    if (DeviceInformationError is { } informationError) await SendAsync(socket, new { c, err = informationError });
                    else await SendAsync(socket, new { c, device = information });
                    break;
                case "open-window": await OpenMirrorAsync(socket, request); break;
                case "close-window":
                    var sessionId = request.GetProperty("sessionId").GetString()!;
                    ClosedMirrors.Enqueue(sessionId);
                    if (MirrorCloseGate is { } closeGate) await closeGate;
                    _mirrors.TryRemove(sessionId, out _);
                    _mirrorControls.TryRemove(sessionId, out _);
                    await SendAsync(socket, new { c = "window-state", sessionId, state = "closed", pkg = "" });
                    break;
                case "video-ack": VideoAcks.Enqueue((request.GetProperty("sessionId").GetString()!, request.GetProperty("bytes").GetUInt64())); break;
                case "app-list":
                    await SendAsync(socket, new { c, apps = Enumerable.Range(0, 18).Select(i => new { packageName = "test.app" + i, displayName = i == 0 ? "相册" : "测试应用 " + i, userId = i == 17 ? 10 : 0, isSystemApp = false, appCategory = -1, game = i == 1 }) }); break;
                case "app-icons":
                    var items = request.GetProperty("items").EnumerateArray().ToArray();
                    IconBatches.Add(items.Length);
                    await SendAsync(socket, new { c, icons = items.Select(i => new { packageName = i.GetProperty("packageName").GetString(), userId = i.GetProperty("userId").GetInt32(), pngBase64 = Png }) }); break;
                case "fs-roots": await SendAsync(socket, new { c = "fs-roots-result", reqId, roots = new[] { new { path = "/storage/emulated/0", name = "内部存储" } } }); break;
                case "fs-list":
                    var path = request.GetProperty("path").GetString()!;
                    FileListRequests.Enqueue(path);
                    if (path.EndsWith("/drop")) { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "test", CancellationToken.None); return; }
                    if (path.EndsWith("/denied")) { await SendAsync(socket, new { c = "fs-list-result", reqId, err = "eacces" }); break; }
                    if (FileListings.TryGetValue(path, out var listing))
                    {
                        await SendAsync(socket, new { c = "fs-list-result", reqId, path, entries = listing });
                        break;
                    }
                    var second = request.TryGetProperty("cursor", out var cursor);
                    SawStringCursor |= second && cursor.ValueKind == JsonValueKind.String && cursor.GetString() == "200";
                    var reply = new Dictionary<string, object?> { ["c"] = "fs-list-result", ["reqId"] = reqId, ["path"] = path,
                        ["entries"] = Enumerable.Range(second ? 200 : 0, second ? 1 : 200).Select(i => new { name = i == 200 ? "Download" : $"文件 {i:D3}.txt", isDir = i == 200, size = i * 100, mtime = 1700000000000L, mime = "text/plain" }) };
                    if (!second) reply["nextCursor"] = "200";
                    await SendAsync(socket, reply); break;
                case "fs-mkdir":
                    var parentPath = request.GetProperty("path").GetString()!;
                    var folderName = request.GetProperty("name").GetString()!;
                    CreateDirectoryRequests.Enqueue((parentPath, folderName));
                    if (FileMkdirReplyGate is { } mkdirGate) await mkdirGate.Task.WaitAsync(_app.Lifetime.ApplicationStopping);
                    string? mkdirError = FileMkdirError;
                    if (mkdirError is null && FileListings.TryGetValue(parentPath, out var siblings) &&
                        siblings.Any(entry => string.Equals(entry.Name, folderName, StringComparison.Ordinal))) mkdirError = "eexist";
                    if (mkdirError is not null)
                    {
                        await SendAsync(socket, new { c = "fs-op-result", reqId, op = "mkdir", ok = false, err = mkdirError });
                        break;
                    }
                    FileListings.AddOrUpdate(parentPath, [new(folderName, true, 0, 0, null)],
                        (_, entries) => [.. entries, new(folderName, true, 0, 0, null)]);
                    FileListings[parentPath.TrimEnd('/') + "/" + folderName] = [];
                    await SendAsync(socket, new { c = "fs-op-result", reqId, op = FileMkdirReplyOperation, ok = true });
                    break;
                case "fs-delete":
                    var deletePaths = request.GetProperty("paths").EnumerateArray().Select(static path => path.GetString()!).ToArray();
                    DeleteRequests.Enqueue(deletePaths);
                    if (FileDeleteError is not null)
                    {
                        await SendAsync(socket, new { c = "fs-op-result", reqId, op = "delete", ok = false, err = FileDeleteError });
                        break;
                    }
                    foreach (string deletePath in deletePaths) DeleteMockPath(deletePath);
                    await SendAsync(socket, new { c = "fs-op-result", reqId, op = "delete", ok = true });
                    break;
                case "fs-download":
                    var file = request.GetProperty("path").GetString()!;
                    DownloadPaths.Enqueue(file);
                    var data = file.EndsWith("/empty") ? [] : Payload;
                    var truncated = file.EndsWith("/truncated");
                    const uint id = 73;
                    await SendAsync(socket, new { id, name = "sample.bin", size = data.Length, reqId }, channel: 5, type: 6);
                    await SendAsync(socket, new { c = "fs-op-result", reqId, op = "download", ok = true });
                    if (!truncated)
                    {
                        for (var offset = 0; offset < data.Length; offset += 262144)
                        {
                            var length = Math.Min(262144, data.Length - offset);
                            var chunk = new byte[length + 6]; chunk[0] = 5; chunk[1] = 7;
                            BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(2), id); data.AsSpan(offset, length).CopyTo(chunk.AsSpan(6));
                            await SendBytesAsync(socket, chunk, false);
                            await ReadAckAsync(socket);
                        }
                    }
                    await SendAsync(socket, new { id }, channel: 5, type: 8);
                    if (!truncated)
                    {
                        await ReadAckAsync(socket);
                        if (data.Length > 0) FinalAck.TrySetResult();
                    }
                    break;
            }
        }
    }
    private void DeleteMockPath(string path)
    {
        string deleted = path.TrimEnd('/');
        foreach (string directory in FileListings.Keys.ToArray())
        {
            string prefix = directory.TrimEnd('/') + "/";
            if (string.Equals(directory, deleted, StringComparison.Ordinal) ||
                directory.StartsWith(deleted + "/", StringComparison.Ordinal))
            {
                FileListings.TryRemove(directory, out _);
                continue;
            }

            if (!path.StartsWith(prefix, StringComparison.Ordinal) || path[prefix.Length..].Contains('/')) continue;
            string leaf = path[prefix.Length..];
            FileListings[directory] = FileListings[directory].Where(entry => !string.Equals(entry.Name, leaf, StringComparison.Ordinal)).ToArray();
        }
    }

    private async Task ReadAckAsync(WebSocket socket)
    {
        var (_, bytes) = await ReadAsync(socket);
        if (bytes.Length < 2 || bytes[0] != 5 || bytes[1] != 10) throw new IOException("Expected download ACK");
        using var doc = JsonDocument.Parse(bytes.AsMemory(2));
        Acks.Add(doc.RootElement.GetProperty("received").GetInt64());
    }
    private static Task SendAsync(WebSocket socket, object value, bool text = false, byte channel = 4, byte type = 1)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(value);
        return SendBytesAsync(socket, text ? json : new byte[] { channel, type }.Concat(json).ToArray(), text);
    }
    private static async Task SendBytesAsync(WebSocket socket, byte[] bytes, bool text)
    {
        // One WebSocket writer: command replies and unsolicited clipboard notifications share it.
        var send = Sends.GetValue(socket, _ => new SemaphoreSlim(1, 1));
        await send.WaitAsync();
        try
        {
            var kind = text ? WebSocketMessageType.Text : WebSocketMessageType.Binary;
            var mid = bytes.Length / 2;
            await socket.SendAsync(bytes.AsMemory(0, mid), kind, false, CancellationToken.None);
            await socket.SendAsync(bytes.AsMemory(mid), kind, true, CancellationToken.None);
        }
        finally { send.Release(); }
    }
    private static async Task<(WebSocketMessageType, byte[])> ReadAsync(WebSocket socket)
    {
        using var data = new MemoryStream(); var buffer = new byte[65536]; ValueWebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer.AsMemory(), CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close) throw new IOException("Closed");
            data.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return (result.MessageType, data.ToArray());
    }
    public async ValueTask DisposeAsync() { await _app.StopAsync(); await _app.DisposeAsync(); }
}
