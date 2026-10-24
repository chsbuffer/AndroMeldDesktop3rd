using Andromeld.Protocol;

using ReactiveUI;

namespace Andromeld.Desktop.ViewModels;

public sealed partial class FileBrowserViewModel
{
    public async Task DeleteSelectionAsync(CancellationToken cancellationToken)
    {
        if (!CanDeleteSelection || _client is null) return;
        string[] paths = SelectedItems.Select(static item => item.Path).ToArray();
        if (paths.Length == 0) return;

        AndromeldClient client = _client;
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(_connection.Token, cancellationToken);
        CancellationToken ct = linked.Token;
        FilesLoading = true;
        FilesStatus = "正在删除…";
        try
        {
            for (int offset = 0; offset < paths.Length; offset += 64)
            {
                string[] batch = paths[offset..Math.Min(offset + 64, paths.Length)];
                await client.DeleteAsync(batch, ct).ConfigureAwait(true);
            }

            ct.ThrowIfCancellationRequested();
            Selection.Clear();
            await NavigateCoreAsync(CurrentPath, recordHistory: false).ConfigureAwait(true);
            FilesStatus = $"已删除 {paths.Length} 项";
        }
        catch (OperationCanceledException)
        {
            FilesStatus = "删除已取消";
            throw;
        }
        finally
        {
            FilesLoading = false;
            this.RaisePropertyChanged(nameof(FilesEmpty));
        }
    }
}
