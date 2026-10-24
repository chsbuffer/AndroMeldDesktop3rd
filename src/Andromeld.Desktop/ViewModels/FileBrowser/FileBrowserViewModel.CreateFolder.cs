using Andromeld.Protocol;

using Avalonia.Threading;

namespace Andromeld.Desktop.ViewModels;

public sealed partial class FileBrowserViewModel
{
    public async Task CreateFolderAsync(string name, CancellationToken cancellationToken)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!CanCreateFolder || _client is null || _directory is null) return;

        AndromeldClient client = _client;
        string parent = CurrentPath;
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(_connection.Token, _directory.Token, cancellationToken);
        CreatingFolder = true;
        Error = string.Empty;
        FilesStatus = "正在创建文件夹…";
        try
        {
            await client.CreateDirectoryAsync(parent, name, linked.Token).ConfigureAwait(true);
            linked.Token.ThrowIfCancellationRequested();
            await NavigateCoreAsync(parent, recordHistory: false).ConfigureAwait(true);
            FileBrowserItem? created = VisibleFiles.FirstOrDefault(item => item.IsDir && string.Equals(item.Name, name, StringComparison.Ordinal));
            if (created is not null) Selection.Select(VisibleFiles.IndexOf(created));
            FilesStatus = $"已创建文件夹“{name}”";
        }
        catch
        {
            if (!linked.IsCancellationRequested) FilesStatus = "创建文件夹失败";
            throw;
        }
        finally
        {
            CreatingFolder = false;
        }
    }
}
