using Andromeld.Protocol;
using Andromeld.Testing;

namespace Andromeld.Protocol.Tests;

public static partial class Program
{
    private static async Task CheckFileDragAsync(AndromeldClient client, MockPhone server, PhoneApp app)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var source = new MemoryStream(MockPhone.Payload);
        var uploaded = await client.UploadAsync("中文.bin", source.Length, source, "/storage/emulated/0", null, null, stop.Token);
        var received = server.Uploads.Last();
        Check(received.Data.SequenceEqual(MockPhone.Payload) && received.Begin.GetProperty("destPath").GetString() == "/storage/emulated/0" &&
            !received.Begin.TryGetProperty("sessionId", out _) && uploaded.Name == "中文.bin" && !uploaded.Imported,
            "upload streams BE32 chunks to a directory and waits for upload-done");
        using var empty = new MemoryStream();
        var imported = await client.UploadAsync("empty", 0, empty, null, "mirror-target", null, stop.Token);
        Check(imported.Imported && server.Uploads.Last().Data.Length == 0 && server.Uploads.Last().Begin.GetProperty("sessionId").GetString() == "mirror-target",
            "empty upload imports into the specified mirror session");
        using var changed = new MemoryStream([1, 2, 3]);
        try { await client.UploadAsync("changed", 2, changed, null, null, null, stop.Token); throw new Exception("Accepted changed length"); }
        catch (IOException) { await UntilAsync(() => !server.UploadAborts.IsEmpty, stop.Token); Check(true, "changed upload length aborts the Android upload slot"); }
        server.RejectUploads = true;
        try { await client.UploadAsync("rejected", 0, empty, "/bad", null, null, stop.Token); throw new Exception("Accepted upload failure"); }
        catch (IOException ex) { Check(ex.Message.Contains("bad-dest"), "upload-failed surfaces Android destination errors"); }
        server.RejectUploads = false;

        await using var mirror = await client.OpenMirrorAsync(app, stop.Token);
        await mirror.AttachAsync(stop.Token);
        TaskCompletionSource<DragOutTransfer> offered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        mirror.DragOutStarted += transfer => offered.TrySetResult(transfer);
        await server.PushDragOutAsync(mirror.SessionId);
        var drag = await offered.Task.WaitAsync(stop.Token);
        await drag.HandoffAsync(123, 234, stop.Token);
        var items = await drag.Metadata.WaitAsync(stop.Token);
        Check(drag.TransactionId == MockPhone.DragTransaction && items.Length == 2 && items[0].Name == "图片.png" && items[1].Size == -1,
            "drag metadata preserves int64 transaction string, Unicode names and unknown size");
        using var output = new MemoryStream();
        await drag.ReceiveItemAsync(items[0], output, stop.Token);
        using var zero = new MemoryStream();
        await drag.ReceiveItemAsync(items[1], zero, stop.Token);
        Check(output.ToArray().SequenceEqual(MockPhone.Payload) && zero.Length == 0 &&
            server.DragAcks.Select(a => a.Offset).SequenceEqual([262144L, 524288L, 600000L]),
            "drag-out writes sequential chunks before cumulative ACK and validates EOF SHA-256 including empty files");
        await drag.FinishAsync(true, stop.Token);
        await UntilAsync(() => server.DragTerminals.Any(t => t.Status == 1), stop.Token);
        Check(true, "successful native receiver explicitly completes the Android transaction");
        foreach (var badOffset in new[] { false, true })
        {
            offered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await server.PushDragOutAsync(mirror.SessionId, badHash: !badOffset, badOffset: badOffset);
            var invalid = await offered.Task.WaitAsync(stop.Token);
            await invalid.HandoffAsync(1, 2, stop.Token);
            var invalidItems = await invalid.Metadata.WaitAsync(stop.Token);
            using var invalidOutput = new MemoryStream();
            try { await invalid.ReceiveItemAsync(invalidItems[0], invalidOutput, stop.Token); throw new Exception("Accepted invalid drag file"); }
            catch (IOException) { Check(true, badOffset ? "out-of-order drag chunks are rejected" : "corrupt drag SHA-256 is rejected"); }
        }
        await mirror.TextAsync("still usable", stop.Token);
        Check((await client.GetRootsAsync(stop.Token)).Length > 0, "file transfer failures keep the authenticated device and mirror usable");
    }

    private static async Task CheckFileDeleteAsync(AndromeldClient client, MockPhone server, string root)
    {
        string removable = root + "/delete-me.txt";
        server.FileListings[root] =
        [
            new FileEntry("delete-me.txt", false, 1, 0, "text/plain"),
            new FileEntry("keep.txt", false, 1, 0, "text/plain")
        ];
        await client.DeleteAsync([removable], CancellationToken.None);
        Check(server.DeleteRequests.TryPeek(out string[]? requested) && requested.SequenceEqual([removable]),
            "fs-delete sends the selected remote paths");
        var remaining = await client.ListAsync(root, CancellationToken.None);
        Check(remaining.Select(entry => entry.Name).SequenceEqual(["keep.txt"]), "fs-delete removes only the requested mock entry");

        try
        {
            await client.DeleteAsync(Enumerable.Range(0, 65).Select(i => $"{root}/{i}").ToArray(), CancellationToken.None);
            throw new Exception("Delete batch over 64 accepted");
        }
        catch (ArgumentOutOfRangeException) { Check(true, "fs-delete rejects batches over 64 before sending"); }

        server.FileDeleteError = "eacces";
        try
        {
            await client.DeleteAsync([root + "/keep.txt"], CancellationToken.None);
            throw new Exception("Delete error accepted");
        }
        catch (IOException ex) { Check(ex.Message.Contains("eacces"), "fs-delete surfaces Android errors"); }
        server.FileDeleteError = null;
    }
}
