using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text.Json;

namespace Andromeld.Protocol;

public sealed partial class AndromeldClient
{
    private static readonly SearchValues<char> InvalidNameCharacters = SearchValues.Create("/\\\0");
    private readonly SemaphoreSlim _uploadSlots = new(4, 4);
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<UploadResult>> _uploads = new();
    private int _nextUpload;

    /// <summary>Caller owns the source until completion. At most four uploads per connection.</summary>
    public async Task<UploadResult> UploadAsync(string name, long size, Stream source, string? destinationDirectory,
        string? sessionId, IProgress<long>? progress, CancellationToken ct)
    {
        if (size is < 0 or > 4L * 1024 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(size), "文件不能超过 4 GiB。");
        }

        if (string.IsNullOrWhiteSpace(name) || name.AsSpan().IndexOfAny(InvalidNameCharacters) >= 0 || name is "." or "..")
        {
            throw new ArgumentException("无效文件名。", nameof(name));
        }

        if (destinationDirectory is not null && sessionId is not null)
        {
            throw new ArgumentException("不能同时指定目录和镜像。", nameof(destinationDirectory));
        }

        if (!await _uploadSlots.WaitAsync(0, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException("请等待正在上传的文件。");
        }

        uint id = unchecked((uint)Interlocked.Increment(ref _nextUpload));
        TaskCompletionSource<UploadResult> done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_uploads.TryAdd(id, done))
        {
            _uploadSlots.Release();
            throw new InvalidOperationException("上传编号已占用。");
        }

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        try
        {
            Dictionary<string, object> begin = new(StringComparer.Ordinal) { ["id"] = id, ["name"] = name, ["size"] = size, ["mime"] = "application/octet-stream" };
            if (destinationDirectory is not null)
            {
                begin["destPath"] = destinationDirectory;
            }

            if (sessionId is not null)
            {
                begin["sessionId"] = sessionId;
            }

            await SendJsonAsync(begin, false, 5, 1, linked.Token).ConfigureAwait(false);
            byte[] packet = new byte[(64 * 1024) + 6];
            packet[0] = 5;
            packet[1] = 2;
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(2), id);
            long sent = 0;
            while (true)
            {
                int count = await source.ReadAsync(packet.AsMemory(6), linked.Token).ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }

                if (sent + count > size)
                {
                    throw new IOException("上传期间文件大小发生变化。");
                }

                if (done.Task.IsCompleted)
                {
                    await done.Task.ConfigureAwait(false);
                }

                await SendBytesAsync(packet.AsMemory(0, count + 6), false, linked.Token).ConfigureAwait(false);
                sent += count;
                progress?.Report(sent);
            }

            if (sent != size)
            {
                throw new IOException("上传期间文件大小发生变化。");
            }

            await SendJsonAsync(new { id }, false, 5, 3, linked.Token).ConfigureAwait(false);
            return await done.Task.WaitAsync(TimeSpan.FromSeconds(35), linked.Token).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                using CancellationTokenSource abort = new(TimeSpan.FromSeconds(2));
                await SendJsonAsync(new { id }, false, 5, 4, abort.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                /* Preserve the upload failure. */
            }

            throw;
        }
        finally
        {
            _uploads.TryRemove(id, out _);
            _uploadSlots.Release();
        }
    }

    private bool HandleUploadControl(JsonElement value)
    {
        if (!value.TryGetProperty("c", out JsonElement c) || c.GetString() is not ("upload-done" or "upload-failed"))
        {
            return false;
        }

        if (value.TryGetProperty("id", out JsonElement identifier) && identifier.TryGetUInt32(out uint id) &&
            _uploads.TryGetValue(id, out TaskCompletionSource<UploadResult>? done))
        {
            if (string.Equals(c.GetString(), "upload-failed", StringComparison.Ordinal))
            {
                done.TrySetException(new IOException("手机接收文件失败：" + value.GetProperty("reason").GetString()));
            }
            else
            {
                done.TrySetResult(new UploadResult(value.GetProperty("name").GetString() ?? "",
                    value.TryGetProperty("imported", out JsonElement imported) && imported.GetBoolean()));
            }
        }

        return true;
    }
}