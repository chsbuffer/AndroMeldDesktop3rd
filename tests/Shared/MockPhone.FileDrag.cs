using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace Andromeld.Testing;

public sealed partial class MockPhone
{
    private readonly Dictionary<uint, (JsonElement Begin, MemoryStream Data)> _incomingUploads = new();
    public ConcurrentQueue<(JsonElement Begin, byte[] Data)> Uploads { get; } = new();
    public ConcurrentQueue<uint> UploadAborts { get; } = new();
    public bool RejectUploads { get; set; }
    private async Task HandleUploadAsync(WebSocket socket, byte[] packet)
    {
        var type = packet[1];
        if (type == 2)
        {
            var id = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(2));
            if (_incomingUploads.TryGetValue(id, out var current)) current.Data.Write(packet.AsSpan(6));
            return;
        }
        using var document = JsonDocument.Parse(packet.AsMemory(2));
        var value = document.RootElement;
        var identifier = value.GetProperty("id").GetUInt32();
        if (type == 1)
        {
            if (RejectUploads) { await SendAsync(socket, new { c = "upload-failed", id = identifier, reason = "bad-dest" }); return; }
            if (value.TryGetProperty("destPath", out _) && value.TryGetProperty("sessionId", out _)) throw new IOException("Conflicting upload destinations");
            _incomingUploads.Add(identifier, (value.Clone(), new()));
        }
        else if (type == 3 && _incomingUploads.Remove(identifier, out var upload))
        {
            using var data = upload.Data;
            if (data.Length != upload.Begin.GetProperty("size").GetInt64()) throw new IOException("Upload length mismatch");
            Uploads.Enqueue((upload.Begin, data.ToArray()));
            await SendAsync(socket, new { c = "upload-done", id = identifier, name = upload.Begin.GetProperty("name").GetString(), imported = upload.Begin.TryGetProperty("sessionId", out _) });
        }
        else if (type == 4)
        {
            UploadAborts.Enqueue(identifier);
            if (_incomingUploads.Remove(identifier, out var aborted)) aborted.Data.Dispose();
        }
    }

    private readonly ConcurrentDictionary<string, WebSocket> _mediaSockets = new();
    private readonly ConcurrentDictionary<string, DragFixture> _dragFixtures = new();
    public ConcurrentQueue<(string Session, long Offset)> DragAcks { get; } = new();
    public ConcurrentQueue<(string Session, int Status)> DragTerminals { get; } = new();
    public const string DragTransaction = "9007199254740993";
    private sealed class DragFixture
    {
        public bool BadHash, BadOffset;
        public int Item;
        public long Request, Offset;
    }
    public async Task PushDragOutAsync(string sessionId, bool badHash = false, bool badOffset = false)
    {
        _dragFixtures[sessionId] = new() { BadHash = badHash, BadOffset = badOffset };
        await SendAsync(_mediaSockets[sessionId], new { c = "drag-out", t = "live", txn = DragTransaction });
    }
    private async Task HandleDragOutAsync(WebSocket socket, string sessionId, JsonElement value)
    {
        if (value.GetProperty("txn").GetString() != DragTransaction) throw new IOException("Lost transaction precision");
        var fixture = _dragFixtures[sessionId];
        switch (value.GetProperty("c").GetString())
        {
            case "drag-out-handoff":
                if (!value.TryGetProperty("x", out _) || !value.TryGetProperty("y", out _)) throw new IOException("Missing handoff pointer");
                await SendAsync(socket, new { c = "drag-out", t = "handoff-ack", txn = DragTransaction, status = 1 });
                await SendAsync(socket, new { c = "drag-out", t = "metadata", txn = DragTransaction, items = new[]
                { new { i = 0, name = "图片.png", mime = "image/png", size = (long)Payload.Length }, new { i = 1, name = "empty.txt", mime = "text/plain", size = -1L } } });
                break;
            case "drag-out-transfer-start":
                fixture.Item = value.GetProperty("item").GetInt32(); fixture.Request = value.GetProperty("req").GetInt64(); fixture.Offset = 0;
                if (value.GetProperty("len").GetInt32() != 262144) throw new IOException("Incorrect drag chunk request");
                await SendAsync(socket, new { c = "drag-out", t = "transfer-started", txn = DragTransaction, req = fixture.Request, item = fixture.Item });
                await SendDragChunkAsync(socket, fixture);
                break;
            case "drag-out-chunk-ack":
                var offset = value.GetProperty("offset").GetInt64();
                if (offset != fixture.Offset || value.GetProperty("req").GetInt64() != fixture.Request || value.GetProperty("item").GetInt32() != fixture.Item)
                    throw new IOException("Incorrect cumulative drag ACK");
                DragAcks.Enqueue((sessionId, offset));
                await SendDragChunkAsync(socket, fixture);
                break;
            case "drag-out-terminal": DragTerminals.Enqueue((sessionId, value.GetProperty("status").GetInt32())); break;
        }
    }
    private static Task SendDragChunkAsync(WebSocket socket, DragFixture fixture)
    {
        var payload = fixture.Item == 0 ? Payload : [];
        if (fixture.Offset == payload.Length)
            return SendAsync(socket, new { c = "drag-out", t = "item-done", txn = DragTransaction, req = fixture.Request, item = fixture.Item,
                size = payload.Length, status = 1, sha = fixture.BadHash ? new string('0', 64) : Convert.ToHexStringLower(SHA256.HashData(payload)) });
        var count = (int)Math.Min(262144, payload.Length - fixture.Offset);
        var packet = new byte[31 + count]; packet[0] = 5; packet[1] = 5;
        BinaryPrimitives.WriteInt64BigEndian(packet.AsSpan(2), long.Parse(DragTransaction));
        BinaryPrimitives.WriteInt64BigEndian(packet.AsSpan(10), fixture.Request);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(18), fixture.Item);
        BinaryPrimitives.WriteInt64BigEndian(packet.AsSpan(22), fixture.Offset + (fixture.BadOffset ? 1 : 0));
        payload.AsSpan((int)fixture.Offset, count).CopyTo(packet.AsSpan(31));
        fixture.Offset += count;
        return SendBytesAsync(socket, packet, false);
    }
}
