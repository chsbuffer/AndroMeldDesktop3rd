using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;

namespace Andromeld.Protocol;

/// <summary>LAN or ADB-forwarded WebSocket transport. Unlike RTC, it has no eight-byte fragment header.</summary>
public sealed partial class AndromeldClient : IAsyncDisposable
{
    public const string Origin = "https://app.andromeld.catchingnow.com";

    private const int MaxMessage = 8 * 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ClientWebSocket _socket = new();

    private readonly CancellationTokenSource _lifetime = new();

    private readonly SemaphoreSlim _send = new(1, 1);

    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new(StringComparer.Ordinal);

    private readonly TaskCompletionSource<JsonElement> _welcome = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Task? _reader;

    private Transfer? _transfer;

    private bool _disposed;

    public event Action<string>? PairingCode;

    public event Action<Exception>? Disconnected;

    public string DeviceName { get; private set; } = "Android";

    public string? DeviceId { get; private set; }

    public string? Token { get; private set; }

    public HashSet<string> Capabilities { get; private set; } = [];

    public Task ConnectAsync(Uri endpoint, string clientId, string? savedToken, string? code, CancellationToken ct) =>
        ConnectCoreAsync(endpoint, clientId, savedToken, string.IsNullOrWhiteSpace(code) ? null : ConnectCode.Ticket(code), ct);

    public Task ConnectUsbAsync(Uri endpoint, string clientId, string? savedToken, UsbBootstrap bootstrap, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(bootstrap);
        if (!UsbBootstrap.IsValidTicket(bootstrap.Ticket)) throw new ArgumentException("USB ticket 无效。", nameof(bootstrap));
        return ConnectCoreAsync(endpoint, clientId, savedToken, string.IsNullOrEmpty(savedToken) ? bootstrap.Ticket : null, ct);
    }

    private async Task ConnectCoreAsync(Uri endpoint, string clientId, string? savedToken, string? ticket, CancellationToken ct)
    {
        _endpoint = endpoint;
        _socket.Options.Proxy = null;
        _socket.Options.SetRequestHeader(nameof(Origin), Origin);
        _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        using var dial = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        dial.CancelAfter(TimeSpan.FromSeconds(8));
        await _socket.ConnectAsync(endpoint, dial.Token).ConfigureAwait(false);
        _reader = ReceiveLoopAsync();
        string platform;
        if (OperatingSystem.IsWindows())
        {
            platform = "windows";
        }
        else if (OperatingSystem.IsMacOS())
        {
            platform = "macos";
        }
        else
        {
            platform = "linux";
        }

        var client = new { id = clientId, platform, name = "AndroMeld Avalonia" };
        object auth;
        if (ticket is not null)
        {
            auth = new { t = "bootstrap", v = 2, client, ticket };
        }
        else if (!string.IsNullOrEmpty(savedToken))
        {
            auth = new { t = "resume", v = 2, client, token = savedToken };
        }
        else
        {
            auth = new { t = "pair", v = 2, client };
        }
        await SendJsonAsync(auth, true, 0, 0, ct).ConfigureAwait(false);
        var welcome = await _welcome.Task.WaitAsync(TimeSpan.FromSeconds(250), ct).ConfigureAwait(false);
        DeviceName = welcome.TryGetProperty("device", out var device) && device.TryGetProperty("name", out var name) ? name.GetString() ?? "Android" : "Android";
        DeviceId = device.ValueKind == JsonValueKind.Object && device.TryGetProperty("id", out var deviceId) ? deviceId.GetString() : null;
        Token = welcome.TryGetProperty("token", out var token) ? token.GetString() : savedToken;
        Capabilities = welcome.TryGetProperty("caps", out var caps) ? caps.EnumerateArray().Select(static x => x.GetString()!).ToHashSet(StringComparer.Ordinal) : [];
        if (welcome.TryGetProperty("sessions", out var sessions))
        {
            foreach (var session in sessions.EnumerateArray())
            {
                var state = session.Deserialize<MirrorState>(Json);
                if (state is not null) _sessions[state.SessionId] = state;
            }
        }
    }

    public async Task<PhoneApp[]> GetAppsAsync(CancellationToken ct) =>
        (await RequestAsync("app-list", new { c = "app-list" }, ct).ConfigureAwait(false)).GetProperty("apps").Deserialize<PhoneApp[]>(Json) ?? [];

    public async Task<DeviceInformation> GetDeviceInformationAsync(CancellationToken ct)
    {
        var reply = await RequestAsync("device-info", new { c = "device-info" }, ct).ConfigureAwait(false);
        if (!reply.TryGetProperty("device", out var device) || device.ValueKind != JsonValueKind.Object)
        {
            throw new IOException("手机未返回设备信息。");
        }
        return device.Deserialize<DeviceInformation>(Json)!;
    }

    public async Task<AppIcon[]> GetIconsAsync(IEnumerable<PhoneApp> apps, CancellationToken ct)
    {
        var items = apps.Select(static a => new { packageName = a.PackageName, userId = a.UserId }).ToArray();
        if (items.Length is < 1 or > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(apps));
        }
        return (await RequestAsync("app-icons", new { c = "app-icons", items }, ct).ConfigureAwait(false)).GetProperty("icons").Deserialize<AppIcon[]>(Json) ?? [];
    }

    public async Task<StorageRoot[]> GetRootsAsync(CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        StorageRootsReply reply = await RequestAsync<StorageRootsReply>(id, new { c = "fs-roots", reqId = id }, ct).ConfigureAwait(false);
        return reply.Roots ?? [];
    }

    public async Task<FileEntry[]> ListAsync(string path, CancellationToken ct)
    {
        var entries = new List<FileEntry>();
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            var id = Guid.NewGuid().ToString("N");
            var request = new Dictionary<string, object>(StringComparer.Ordinal) { ["c"] = "fs-list", ["reqId"] = id, ["path"] = path };
            if (cursor is not null)
            {
                request["cursor"] = cursor;
            }
            FileListReply reply = await RequestAsync<FileListReply>(id, request, ct).ConfigureAwait(false);
            entries.AddRange(reply.Entries ?? []);
            cursor = reply.NextCursor;
            if (!string.IsNullOrEmpty(cursor) && !cursors.Add(cursor))
            {
                throw new IOException("设备返回了重复的目录分页游标。");
            }
        } while (!string.IsNullOrEmpty(cursor));
        return entries.OrderByDescending(static e => e.IsDir).ThenBy(static e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public async Task CreateDirectoryAsync(string path, string name, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!FileNameRules.IsValid(name))
        {
            throw new ArgumentException("文件夹名称无效。", nameof(name));
        }

        var id = Guid.NewGuid().ToString("N");
        FileOperationResult result = await RequestAsync<FileOperationResult>(id, new { c = "fs-mkdir", reqId = id, path, name }, ct).ConfigureAwait(false);
        if (!string.Equals(result.C, "fs-op-result", StringComparison.Ordinal) ||
            !string.Equals(result.Op, "mkdir", StringComparison.Ordinal) ||
            !result.Ok)
        {
            throw new IOException("手机返回了无效的创建文件夹响应。");
        }
    }

    public async Task DeleteAsync(IReadOnlyList<string> paths, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count is < 1 or > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(paths), "一次删除 1–64 个路径。");
        }
        if (paths.Any(static path => string.IsNullOrWhiteSpace(path)))
        {
            throw new ArgumentException("删除路径不能为空。", nameof(paths));
        }

        var id = Guid.NewGuid().ToString("N");
        FileOperationResult result = await RequestAsync<FileOperationResult>(id, new { c = "fs-delete", reqId = id, paths }, ct).ConfigureAwait(false);
        if (!string.Equals(result.C, "fs-op-result", StringComparison.Ordinal) ||
            !string.Equals(result.Op, "delete", StringComparison.Ordinal) ||
            !result.Ok)
        {
            throw new IOException("手机返回了无效的删除响应。");
        }
    }

    public async Task DownloadAsync(string path, Stream destination, IProgress<(long Received, long Total)>? progress, CancellationToken ct)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        idle.CancelAfter(TimeSpan.FromSeconds(35));
        var transfer = new Transfer(Guid.NewGuid().ToString("N"), destination, progress, idle);
        if (Interlocked.CompareExchange(ref _transfer, transfer, null) is not null)
        {
            throw new InvalidOperationException("请等待当前文件下载完成。");
        }
        try
        {
            await RequestAsync(transfer.RequestId, new { c = "fs-download", reqId = transfer.RequestId, path }, idle.Token).ConfigureAwait(false);
            await transfer.Done.Task.WaitAsync(idle.Token).ConfigureAwait(false);
        }
        catch
        {
            // Even cancellation must tell the sender to release its single download slot.
            if (transfer.Id is { } id && _socket.State == WebSocketState.Open)
            {
                try
                {
                    await SendJsonAsync(new { id }, false, 5, 9, _lifetime.Token).ConfigureAwait(false);
                }
                catch (Exception) { /* Preserve the original failure. */ }
            }

            throw;
        }
        finally
        {
            Interlocked.CompareExchange(ref _transfer, null, transfer);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        _socket.Abort();
        if (_reader is not null)
        {
            await _reader.ConfigureAwait(false);
        }
        _socket.Dispose();
        _lifetime.Dispose();
        _send.Dispose();
    }

    private async Task<JsonElement> RequestAsync(string key, object request, CancellationToken ct)
    {
        var source = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(key, source))
        {
            throw new InvalidOperationException("同类请求仍在进行中。");
        }
        try
        {
            await SendJsonAsync(request, false, 4, 1, ct).ConfigureAwait(false);
            var reply = await source.Task.WaitAsync(TimeSpan.FromSeconds(25), ct).ConfigureAwait(false);
            ProtocolStatus? status = reply.Deserialize<ProtocolStatus>(Json);
            if (!string.IsNullOrEmpty(status?.Err))
            {
                throw new IOException($"手机返回错误：{status.Err}");
            }
            if (status?.Ok is false)
            {
                throw new IOException("手机未能完成文件操作。");
            }
            return reply;
        }
        finally
        {
            _pending.TryRemove(key, out _);
        }
    }

    private async Task<T> RequestAsync<T>(string key, object request, CancellationToken ct)
    {
        JsonElement reply = await RequestAsync(key, request, ct).ConfigureAwait(false);
        try
        {
            return reply.Deserialize<T>(Json) ?? throw new IOException("手机返回了空响应。");
        }
        catch (JsonException ex)
        {
            throw new IOException("手机返回了无效响应。", ex);
        }
    }

    private sealed record ProtocolStatus(string? Err, bool? Ok);
    private sealed record StorageRootsReply(StorageRoot[]? Roots);
    private sealed record FileListReply(FileEntry[]? Entries, string? NextCursor);


    private async Task SendJsonAsync(object value, bool text, byte channel, byte type, CancellationToken ct)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        var bytes = text ? json : new byte[json.Length + 2];
        if (!text)
        {
            bytes[0] = channel;
            bytes[1] = type;
            json.CopyTo(bytes, 2);
        }

        await SendBytesAsync(bytes, text, ct).ConfigureAwait(false);
    }

    private async Task SendBytesAsync(ReadOnlyMemory<byte> bytes, bool text, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        await _send.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            await _socket.SendAsync(bytes, text ? WebSocketMessageType.Text : WebSocketMessageType.Binary, true, linked.Token).ConfigureAwait(false);
        }
        finally
        {
            _send.Release();
        }
    }

    private async Task ReceiveLoopAsync()
    {
        Exception failure = new IOException("设备已断开连接。");
        try
        {
            var buffer = new byte[65536];
            while (!_lifetime.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                ValueWebSocketReceiveResult part;
                do
                {
                    part = await _socket.ReceiveAsync(buffer.AsMemory(), _lifetime.Token).ConfigureAwait(false);
                    if (part.MessageType == WebSocketMessageType.Close)
                    {
                        throw new IOException("设备关闭了连接，请重新连接。");
                    }
                    if (message.Length + part.Count > MaxMessage)
                    {
                        throw new IOException("设备消息超过 8 MiB 上限。");
                    }
                    await message.WriteAsync(buffer.AsMemory(0, part.Count), _lifetime.Token).ConfigureAwait(false);
                } while (!part.EndOfMessage);
                var bytes = message.ToArray();
                if (part.MessageType == WebSocketMessageType.Text)
                {
                    HandleText(bytes);
                }
                else if (bytes.Length >= 2 && bytes[0] == 4 && bytes[1] == 1)
                {
                    HandleControl(bytes.AsMemory(2));
                }
                else if (bytes.Length >= 2 && bytes[0] == 3 && bytes[1] == 1)
                {
                    HandleClipboard(bytes.AsMemory(2));
                }
                else if (bytes.Length >= 2 && bytes[0] == 2)
                {
                    HandleAudio(bytes[1], bytes.AsMemory(2));
                }
                else if (bytes.Length >= 2 && bytes[0] == 5)
                {
                    await HandleFileAsync(bytes[1], bytes.AsMemory(2)).ConfigureAwait(false);
                }
                else if (bytes.Length >= 6 && bytes[0] == 6 && bytes[1] == 1)
                {
                    HandleTerminalOutput(bytes.AsMemory(2));
                }
            }
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            _welcome.TrySetException(failure);
            foreach (var pending in _pending)
            {
                pending.Value.TrySetException(failure);
            }
            Volatile.Read(ref _transfer)?.Done.TrySetException(failure);
            foreach (var upload in _uploads) upload.Value.TrySetException(failure);
            FailTerminals(failure);
            await _lifetime.CancelAsync().ConfigureAwait(false);
            if (!_disposed)
            {
                Disconnected?.Invoke(failure);
            }
        }
    }

    private void HandleText(byte[] bytes)
    {
        using var doc = JsonDocument.Parse(bytes);
        var value = doc.RootElement;
        switch (value.GetProperty("t").GetString())
        {
            case "welcome":
            {
                if (value.GetProperty("proto").GetInt32() != 2)
                {
                    throw new IOException("设备不支持业务协议 v2。");
                }
                _welcome.TrySetResult(value.Clone()); break;
            }
            case "pair-code":
            {
                PairingCode?.Invoke(value.GetProperty("code").ToString()); break;
            }
            case "deny":
            {
                var reason = value.TryGetProperty("reason", out var r) ? r.GetString() : "UNKNOWN";
                _welcome.TrySetException(new AuthenticationDeniedException(reason ?? "UNKNOWN")); break;
            }
        }
    }

    private void HandleControl(ReadOnlyMemory<byte> bytes)
    {
        using var doc = JsonDocument.Parse(bytes);
        var value = doc.RootElement;
        if (value.TryGetProperty("c", out JsonElement notificationCommand) && notificationCommand.ValueKind == JsonValueKind.String &&
            string.Equals(notificationCommand.GetString(), "notif-snapshot", StringComparison.Ordinal))
        {
            HandleNotificationSnapshot(value);
            return;
        }
        if (HandleUploadControl(value)) return;
        if (HandleTerminalControl(value)) return;
        if (value.TryGetProperty("c", out var c) && string.Equals(c.GetString(), "window-state", StringComparison.Ordinal))
        {
            var state = value.Deserialize<MirrorState>(Json)!;
            if (!string.IsNullOrEmpty(state.SessionId))
            {
                if (state.State is "closed" or "failed") _sessions.TryRemove(state.SessionId, out _);
                else _sessions[state.SessionId] = state;
            }
            WindowStateChanged?.Invoke(state);
        }
        string? key = value.TryGetProperty("reqId", out var id) ? id.GetString() : null;
        if (key is null && value.TryGetProperty("c", out var command))
        {
            key = command.GetString();
        }
        if (key is not null && _pending.TryGetValue(key, out var source))
        {
            source.TrySetResult(value.Clone());
        }
    }

    private async Task HandleFileAsync(byte type, ReadOnlyMemory<byte> bytes)
    {
        var transfer = Volatile.Read(ref _transfer);
        if (type == 7)
        {
            if (bytes.Length < 4)
            {
                throw new IOException("文件数据帧缺少传输编号。");
            }
            var chunkId = BinaryPrimitives.ReadUInt32BigEndian(bytes.Span);
            if (transfer is null || transfer.Id != chunkId)
            {
                return;
            }
            try
            {
                if (transfer.Received + bytes.Length - 4 > transfer.Size)
                {
                    throw new IOException("文件大小超出设备声明。");
                }
                await transfer.Stream.WriteAsync(bytes[4..], transfer.Idle.Token).ConfigureAwait(false);
                transfer.Received += bytes.Length - 4;
                transfer.Idle.CancelAfter(TimeSpan.FromSeconds(35));
                transfer.Progress?.Report((transfer.Received, transfer.Size));
                await AckAsync(transfer).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                transfer.Done.TrySetException(ex);
            }

            return;
        }

        if (type is not (6 or 8 or 9))
        {
            return;
        }
        using var doc = JsonDocument.Parse(bytes);
        var value = doc.RootElement;
        var id = value.GetProperty("id").GetUInt32();
        if (type == 6)
        {
            if (transfer is null || !value.TryGetProperty("reqId", out var reqId) ||
                !string.Equals(reqId.GetString(), transfer.RequestId, StringComparison.Ordinal))
            {
                await SendJsonAsync(new { id }, false, 5, 9, _lifetime.Token).ConfigureAwait(false);
                return;
            }

            transfer.Id = id;
            transfer.Size = value.GetProperty("size").GetInt64();
            transfer.Idle.CancelAfter(TimeSpan.FromSeconds(35));
            transfer.Progress?.Report((0, transfer.Size));
        }
        else if (transfer?.Id == id)
        {
            if (type == 9)
            {
                transfer.Done.TrySetException(new IOException("手机中止了文件传输。"));
            }
            else
            {
                try
                {
                    if (transfer.Received != transfer.Size)
                    {
                        throw new IOException("文件传输提前结束，未保存不完整文件。");
                    }
                    await transfer.Stream.FlushAsync(transfer.Idle.Token).ConfigureAwait(false);
                    await AckAsync(transfer).ConfigureAwait(false); // Required even for an empty file.
                    transfer.Done.TrySetResult();
                }
                catch (Exception ex)
                {
                    transfer.Done.TrySetException(ex);
                }
            }
        }
    }

    private Task AckAsync(Transfer transfer) => SendJsonAsync(new { id = transfer.Id!.Value, received = transfer.Received }, false, 5, 10, _lifetime.Token);

    private sealed class Transfer(string requestId, Stream stream, IProgress<(long Received, long Total)>? progress, CancellationTokenSource idle)
    {

        public uint? Id;
        public string RequestId { get; } = requestId;

        public Stream Stream { get; } = stream;

        public IProgress<(long Received, long Total)>? Progress { get; } = progress;

        public CancellationTokenSource Idle { get; } = idle;

        public long Size;

        public long Received;

        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
