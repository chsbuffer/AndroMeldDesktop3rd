using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;

namespace Andromeld.Protocol;

/// <summary>Media reader produces bounded packets; one transfer consumer writes and ACKs them off the UI thread.</summary>
public sealed class DragOutTransfer
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly CancellationToken _lifetime;
    private readonly TaskCompletionSource<DragOutItem[]> _metadata = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<Packet> _packets =
        Channel.CreateBounded<Packet>(new BoundedChannelOptions(8) { SingleReader = true, SingleWriter = true });
    private readonly MirrorSession _session;
    private int _receiving;

    internal DragOutTransfer(MirrorSession session, string transactionId, CancellationToken lifetime)
    {
        _session = session;
        TransactionId = transactionId;
        _lifetime = lifetime;
        TransactionNumber = long.Parse(transactionId, CultureInfo.InvariantCulture);
    }

    public string TransactionId { get; }
    internal long TransactionNumber { get; }
    public Task<DragOutItem[]> Metadata => _metadata.Task;

    public Task HandoffAsync(int x, int y, CancellationToken ct)
    {
        return _session.SendControlAsync(new { c = "drag-out-handoff", txn = TransactionId, x, y }, ct);
    }

    public Task FinishAsync(bool success, CancellationToken ct)
    {
        return _session.SendControlAsync(new { c = "drag-out-terminal", txn = TransactionId, status = success ? 1 : 17 }, ct);
    }

    internal void Fail(Exception ex)
    {
        _metadata.TrySetException(ex);
        _packets.Writer.TryComplete(ex);
    }

    internal void Control(JsonElement value)
    {
        string? kind = value.GetProperty("t").GetString();
        if (string.Equals(kind, "metadata", StringComparison.Ordinal))
        {
            DragOutItem[] items = value.GetProperty("items").Deserialize<DragOutItem[]>(Json) ?? [];
            if (items.Length is < 1 or > 256 || items.Select(static i => i.I).Distinct().Count() != items.Length ||
                Array.Exists(items, static i => i.I < 0 || string.IsNullOrWhiteSpace(i.Name) || i.Size < -1))
            {
                Fail(new IOException("手机返回了无效的拖出文件列表。"));
                return;
            }

            _metadata.TrySetResult(items.OrderBy(static i => i.I).ToArray());
        }
        else if (string.Equals(kind, "handoff-ack", StringComparison.Ordinal) && value.GetProperty("status").GetInt32() != 1)
        {
            Fail(new IOException("手机未能交接文件拖动。"));
        }
        else if (string.Equals(kind, "terminal", StringComparison.Ordinal))
        {
            Fail(new IOException("手机已结束文件拖动。"));
        }
        else if (string.Equals(kind, "item-done", StringComparison.Ordinal))
        {
            Packet packet = new(value.GetProperty("req").GetInt64(), value.GetProperty("item").GetInt32(),
                value.GetProperty("size").GetInt64(), null, value.GetProperty("status").GetInt32(),
                value.TryGetProperty("sha", out JsonElement sha) ? sha.GetString() : null);
            if (!_packets.Writer.TryWrite(packet))
            {
                Fail(new IOException("拖出文件队列超过上限。"));
            }
        }
    }

    internal void Chunk(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length is < 29 or > 29 + 262144)
        {
            Fail(new IOException("拖出文件数据帧长度无效。"));
            return;
        }

        ReadOnlySpan<byte> span = bytes.Span;
        if (span[28] > 1)
        {
            Fail(new IOException("拖出文件结束标记无效。"));
            return;
        }

        if (BinaryPrimitives.ReadInt64BigEndian(span) != TransactionNumber)
        {
            return;
        }

        Packet packet = new(BinaryPrimitives.ReadInt64BigEndian(span[8..]), BinaryPrimitives.ReadInt32BigEndian(span[16..]),
            BinaryPrimitives.ReadInt64BigEndian(span[20..]), bytes[29..], 0, null);
        if (!_packets.Writer.TryWrite(packet))
        {
            Fail(new IOException("拖出文件队列超过上限。"));
        }
    }

    /// <summary>Receive items once, in metadata order; the destination remains caller-owned.</summary>
    public async Task ReceiveItemAsync(DragOutItem item, Stream destination, CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _receiving, 1) != 0)
        {
            throw new InvalidOperationException("拖出文件必须逐项接收。");
        }

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime);
        linked.CancelAfter(TimeSpan.FromSeconds(35));
        CancellationToken token = linked.Token;
        try
        {
            DragOutItem[] items = await Metadata.WaitAsync(token).ConfigureAwait(false);
            if (!items.Contains(item))
            {
                throw new ArgumentException("项目不属于本次拖动。", nameof(item));
            }

            long req = (long)item.I + 1;
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long received = 0;
            await _session.SendControlAsync(new
            {
                c = "drag-out-transfer-start",
                txn = TransactionId,
                req,
                item = item.I,
                len = 262144
            }, token).ConfigureAwait(false);
            while (true)
            {
                Packet packet = await _packets.Reader.ReadAsync(token).ConfigureAwait(false);
                if (packet.Request != req || packet.Item != item.I)
                {
                    throw new IOException("拖出文件项目编号不匹配。");
                }

                if (packet.Data is { } data)
                {
                    if (packet.Offset != received || received + data.Length > 1L << 40 || (item.Size >= 0 && received + data.Length > item.Size))
                    {
                        throw new IOException("拖出文件偏移或大小不匹配。");
                    }

                    await destination.WriteAsync(data, token).ConfigureAwait(false);
                    hash.AppendData(data.Span);
                    received += data.Length;
                    linked.CancelAfter(TimeSpan.FromSeconds(35));
                    await _session.SendControlAsync(new
                    {
                        c = "drag-out-chunk-ack",
                        txn = TransactionId,
                        req,
                        item = item.I,
                        offset = received,
                        len = 262144
                    }, token).ConfigureAwait(false);
                }
                else
                {
                    if (packet.Status != 1 || packet.Offset != received || (item.Size >= 0 && item.Size != received) ||
                        packet.Sha is null || !string.Equals(packet.Sha, Convert.ToHexString(hash.GetHashAndReset()),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        throw new IOException("拖出文件大小或 SHA-256 校验失败。");
                    }

                    await destination.FlushAsync(token).ConfigureAwait(false);
                    return;
                }
            }
        }
        catch
        {
            try
            {
                await _session.SendControlAsync(new { c = "drag-out-terminal", txn = TransactionId, status = 18 }, _lifetime).ConfigureAwait(false);
            }
            catch (Exception)
            {
                /* Preserve transfer failure. */
            }

            throw;
        }
        finally { Volatile.Write(ref _receiving, 0); }
    }

    private sealed record Packet(long Request, int Item, long Offset, ReadOnlyMemory<byte>? Data, int Status, string? Sha);
}