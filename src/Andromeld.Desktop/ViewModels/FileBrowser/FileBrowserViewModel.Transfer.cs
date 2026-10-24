using Andromeld.Desktop.Services;
using Andromeld.Protocol;

using ReactiveUI.SourceGenerators;

namespace Andromeld.Desktop.ViewModels;

public sealed partial class FileBrowserViewModel
{
    public Task UploadFilesAsync(IReadOnlyList<string> paths)
    {
        if (Uploading)
        {
            return _uploadTask ?? Task.CompletedTask;
        }

        _uploadTask = UploadFilesCoreAsync(paths);
        return _uploadTask;
    }

    private async Task UploadFilesCoreAsync(IReadOnlyList<string> paths)
    {
        if (!CanUseFiles || Uploading || _client is null || string.IsNullOrEmpty(CurrentPath))
        {
            return;
        }

        if (paths.Count == 0)
        {
            return;
        }

        if (paths.Any(static path => !File.Exists(path)))
        {
            throw new IOException("请拖入文件；暂不支持拖入整个文件夹。");
        }

        AndromeldClient client = _client;
        string directory = CurrentPath;
        using CancellationTokenSource source = _upload = CancellationTokenSource.CreateLinkedTokenSource(_connection.Token);
        Uploading = true;
        try
        {
            foreach (string path in paths)
            {
                await using FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
                string name = Path.GetFileName(path);
                long length = file.Length;
                Progress<long> progress = new(sent =>
                {
                    if (_upload == source)
                    {
                        UploadStatus = $"{name} · {FileEntry.FormatSize(sent)} / {FileEntry.FormatSize(length)}";
                    }
                });
                UploadStatus = $"正在上传 {name}…";
                await client.UploadAsync(name, file.Length, file, directory, null, progress, source.Token).ConfigureAwait(true);
            }

            UploadStatus = $"已上传 {paths.Count} 个文件";
            if (client == _client && IsAttached && string.Equals(directory, CurrentPath, StringComparison.Ordinal))
            {
                await NavigateAsync(directory).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
            UploadStatus = "上传已取消";
        }
        finally
        {
            _upload = null;
            Uploading = false;
        }
    }

    [ReactiveCommand]
    private void CancelUpload() => _upload?.Cancel();

    private async Task StopUploadsAsync()
    {
        if (_upload is { } upload)
        {
            await upload.CancelAsync().ConfigureAwait(true);
        }

        if (_uploadTask is not null)
        {
            try { await _uploadTask.ConfigureAwait(true); }
            catch (Exception)
            {
                /* The operation owner reports failures. */
            }
        }
    }

    public FileDragBatch CreateFileBatch(IReadOnlyList<FileBrowserItem> entries, CancellationToken windowLifetime,
        string action = "正在拖出", IProgress<string>? progress = null)
    {
        if (!CanUseFiles || _client is null || entries.Count == 0 || entries.Any(static entry => entry.IsDir))
        {
            throw new IOException("请选择要拖出的文件。");
        }

        AndromeldClient client = _client;
        var files = entries.Select(static item => (item.Name, item.Entry.Size, item.Path)).ToArray();
        CancellationToken connection = _connection.Token;
        return new FileDragBatch(async ct =>
        {
            await Task.CompletedTask.ConfigureAwait(false);
            connection.ThrowIfCancellationRequested();
            return files.Select(file => new FileDragSource(file.Name, file.Size, async (stream, token) =>
            {
                using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(connection, token);
                progress?.Report($"{action}：{file.Name} · {FileEntry.FormatSize(file.Size)}");
                await client.DownloadAsync(file.Path, stream, null, linked.Token).ConfigureAwait(false);
            })).ToArray();
        }, windowLifetime);
    }

    public async Task DownloadAsync(FileBrowserItem item, string destination)
    {
        if (!CanTransferFiles || item.IsDir)
        {
            return;
        }

        AndromeldClient client = _client!;
        _download = CancellationTokenSource.CreateLinkedTokenSource(_connection.Token);
        CancellationToken ct = _download.Token;
        string partial = destination + $".{Guid.NewGuid():N}.part";
        Downloading = true;
        DownloadStatus = "准备下载…";
        try
        {
            await using (FileStream stream = new(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                Progress<(long Received, long Total)> progress = new(value =>
                {
                    if (_download?.Token == ct)
                    {
                        DownloadStatus = $"{item.Name} · {FileEntry.FormatSize(value.Received)} / {FileEntry.FormatSize(value.Total)}";
                    }
                });
                await client.DownloadAsync(item.Path, stream, progress, ct).ConfigureAwait(true);
            }

            ct.ThrowIfCancellationRequested();
            File.Move(partial, destination, true);
            DownloadStatus = $"已下载 {item.Name}";
        }
        catch (OperationCanceledException)
        {
            DownloadStatus = "下载已取消";
            throw;
        }
        finally
        {
            try
            {
                if (File.Exists(partial)) File.Delete(partial);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine(ex.Message);
            }

            if (_download?.Token == ct)
            {
                _download.Dispose();
                _download = null;
                Downloading = false;
            }
        }
    }

    public async Task DownloadManyAsync(IReadOnlyList<FileBrowserItem> items, string directory, CancellationToken windowLifetime)
    {
        if (!CanTransferFiles || items.Count == 0 || items.Any(static item => item.IsDir))
        {
            return;
        }

        _download = CancellationTokenSource.CreateLinkedTokenSource(_connection.Token, windowLifetime);
        CancellationToken ct = _download.Token;
        Downloading = true;
        DownloadStatus = "准备下载…";
        try
        {
            var progress = new Progress<string>(message => DownloadStatus = message);
            await using FileDragBatch batch = CreateFileBatch(items, ct, "正在下载", progress);
            await batch.CopyToDirectoryAsync(directory).ConfigureAwait(true);
            DownloadStatus = $"已下载 {items.Count} 个文件";
        }
        catch (OperationCanceledException)
        {
            DownloadStatus = "下载已取消";
            throw;
        }
        finally
        {
            if (_download?.Token == ct)
            {
                _download.Dispose();
                _download = null;
            }
            Downloading = false;
        }
    }

    [ReactiveCommand]
    private void CancelDownload() => _download?.Cancel();
}
