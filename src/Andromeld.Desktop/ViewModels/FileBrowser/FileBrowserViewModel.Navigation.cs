using Andromeld.Protocol;

using ReactiveUI;
using ReactiveUI.SourceGenerators;

namespace Andromeld.Desktop.ViewModels;

public sealed partial class FileBrowserViewModel
{
    public Task NavigateAsync(string path)
    {
        return NavigateCoreAsync(path, recordHistory: true);
    }

    private async Task NavigateCoreAsync(string path, bool recordHistory)
    {
        if (!CanUseFiles || SelectedRoot is null || _client is null)
        {
            return;
        }

        path = path.Trim().TrimEnd('/');
        if (!string.Equals(path, SelectedRoot.Path, StringComparison.Ordinal) &&
            !path.StartsWith(SelectedRoot.Path.TrimEnd('/') + "/", StringComparison.Ordinal))
        {
            throw new IOException("请在手机允许的存储目录中浏览。");
        }

        if (path.Split('/').Any(static part => part is "." or ".."))
        {
            throw new IOException("路径中不能包含 . 或 ..。");
        }

        if (_directory is not null)
        {
            await _directory.CancelAsync().ConfigureAwait(true);
        }

        _directory?.Dispose();
        CancellationTokenSource source = _directory = CancellationTokenSource.CreateLinkedTokenSource(_connection.Token);
        CancellationToken directoryToken = source.Token;
        FilesLoading = true;
        FilesStatus = "正在读取目录…";
        try
        {
            FileEntry[] entries = await _client.ListAsync(path, directoryToken).ConfigureAwait(true);
            directoryToken.ThrowIfCancellationRequested();
            _fileItems.Clear();
            _fileItems.AddRange(entries.Select(entry => new FileBrowserItem(entry, ChildPath(path, entry.Name))));
            Selection.Clear();
            CurrentPath = path;
            PathInput = path;
            RebuildVisibleFiles();
            UpdateFileBreadcrumbs();
            IsEditingFilePath = false;
            FilesStatus = $"{entries.Length} 项 · 文件夹双击打开，文件可下载";
            if (recordHistory) RecordFileHistory(path);
        }
        finally
        {
            if (_directory == source)
            {
                FilesLoading = false;
                this.RaisePropertyChanged(nameof(FilesEmpty));
            }
        }
    }

    private async Task LoadRootsAsync()
    {
        Roots.Clear();
        SelectedRoot = null;
        _fileItems.Clear();
        VisibleFiles.Clear();
        FileBreadcrumbs.Clear();
        Selection.Clear();
        ResetFileHistory();
        if (!CanUseFiles || _client is null)
        {
            FilesStatus = "连接后浏览手机存储空间";
            return;
        }

        try
        {
            AndromeldClient client = _client;
            StorageRoot[] roots = await client.GetRootsAsync(_connection.Token).ConfigureAwait(true);
            if (_client != client) return;
            foreach (StorageRoot root in roots)
            {
                Roots.Add(root);
            }

            if (roots.Length > 0)
            {
                _settingRoot = true;
                SelectedRoot = roots[0];
                _settingRoot = false;
                await NavigateCoreAsync(SelectedRoot.Path, recordHistory: true).ConfigureAwait(true);
            }
            else
            {
                FilesStatus = "手机没有返回可用存储空间";
            }
        }
        catch (OperationCanceledException)
        {
            /* Disconnect or a newer attach owns cancellation. */
        }
        catch (Exception ex)
        {
            Report(ex);
        }
        finally
        {
            this.RaisePropertyChanged(nameof(FilesEmpty));
        }
    }

    private async Task NavigateSafelyAsync(string path)
    {
        try
        {
            await NavigateCoreAsync(path, recordHistory: true).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            /* Cancellation belongs to navigation or disconnect. */
        }
        catch (Exception ex)
        {
            Report(ex);
        }
    }

    private void RebuildVisibleFiles()
    {
        SortFileTree();
        List<FileBrowserItem> rows = [];
        foreach (FileBrowserItem item in _fileItems) Add(item);
        HashSet<FileBrowserItem> visible = rows.ToHashSet();
        for (int index = VisibleFiles.Count - 1; index >= 0; index--)
        {
            if (!visible.Contains(VisibleFiles[index])) VisibleFiles.RemoveAt(index);
        }

        HashSet<FileBrowserItem> existing = VisibleFiles.ToHashSet();
        for (int index = 0; index < rows.Count; index++)
        {
            if (index < VisibleFiles.Count && ReferenceEquals(VisibleFiles[index], rows[index])) continue;
            int previous = existing.Contains(rows[index]) ? VisibleFiles.IndexOf(rows[index]) : -1;
            if (previous >= 0) VisibleFiles.Move(previous, index);
            else VisibleFiles.Insert(index, rows[index]);
        }

        this.RaisePropertyChanged(nameof(FilesEmpty));
        NotifySelectionChanged();

        void Add(FileBrowserItem item)
        {
            rows.Add(item);
            if (ViewMode == FileBrowserViewMode.Tree && item.IsExpanded)
            {
                foreach (FileBrowserItem child in item.Children) Add(child);
            }
        }
    }

    private void UpdateFileBreadcrumbs()
    {
        FileBreadcrumbs.Clear();
        if (SelectedRoot is not { } root || string.IsNullOrEmpty(CurrentPath)) return;
        string rootPath = root.Path.TrimEnd('/');
        FileBreadcrumbs.Add(new FileBreadcrumb(root.Name, rootPath, string.Equals(CurrentPath, rootPath, StringComparison.Ordinal)));
        string path = rootPath;
        foreach (string name in CurrentPath[rootPath.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            path = ChildPath(path, name);
            FileBreadcrumbs.Add(new FileBreadcrumb(name, path, string.Equals(path, CurrentPath, StringComparison.Ordinal)));
        }
    }

    private void RecordFileHistory(string path)
    {
        if (_fileHistoryIndex >= 0 && string.Equals(_fileHistory[_fileHistoryIndex], path, StringComparison.Ordinal))
        {
            NotifyFileHistory();
            return;
        }

        if (_fileHistoryIndex + 1 < _fileHistory.Count)
        {
            _fileHistory.RemoveRange(_fileHistoryIndex + 1, _fileHistory.Count - _fileHistoryIndex - 1);
        }
        _fileHistory.Add(path);
        _fileHistoryIndex = _fileHistory.Count - 1;
        NotifyFileHistory();
    }

    private void ResetFileHistory()
    {
        _fileHistory.Clear();
        _fileHistoryIndex = -1;
        NotifyFileHistory();
    }

    private void NotifyFileHistory()
    {
        this.RaisePropertyChanged(nameof(CanGoBack));
        this.RaisePropertyChanged(nameof(CanGoForward));
    }

    public async Task GoBackAsync()
    {
        if (!CanGoBack) return;
        int target = _fileHistoryIndex - 1;
        await NavigateCoreAsync(_fileHistory[target], recordHistory: false).ConfigureAwait(true);
        _fileHistoryIndex = target;
        NotifyFileHistory();
    }

    public async Task GoForwardAsync()
    {
        if (!CanGoForward) return;
        int target = _fileHistoryIndex + 1;
        await NavigateCoreAsync(_fileHistory[target], recordHistory: false).ConfigureAwait(true);
        _fileHistoryIndex = target;
        NotifyFileHistory();
    }

    [ReactiveCommand(CanExecute = nameof(_canGoBack))]
    private Task BackAsync() => GoBackAsync();

    [ReactiveCommand(CanExecute = nameof(_canGoForward))]
    private Task ForwardAsync() => GoForwardAsync();

    [ReactiveCommand(CanExecute = nameof(_canUseFiles))]
    private Task GoAsync() => NavigateAsync(PathInput);

    [ReactiveCommand(CanExecute = nameof(_canGoUp))]
    private Task Up() => GoUpAsync();

    public Task GoUpAsync()
    {
        return CanGoUp ? NavigateAsync(CurrentPath[..CurrentPath.LastIndexOf('/')]) : Task.CompletedTask;
    }

    [ReactiveCommand(CanExecute = nameof(_canUseFiles))]
    private Task Refresh() => RefreshAsync();

    public Task RefreshAsync() => NavigateAsync(CurrentPath);

    public Task OpenSelectedAsync()
    {
        return Selection.SelectedItem is { IsDir: true } ? NavigateAsync(SelectedFilePath) : Task.CompletedTask;
    }

    internal async Task ToggleFileDirectoryAsync(FileBrowserItem item)
    {
        if (ViewMode != FileBrowserViewMode.Tree || !item.IsDir || item.IsLoading || !CanUseFiles || _directory is null) return;
        if (item.IsExpanded)
        {
            item.IsExpanded = false;
            RebuildVisibleFiles();
            return;
        }

        if (!item.ChildrenLoaded)
        {
            AndromeldClient client = _client!;
            CancellationTokenSource directory = _directory;
            CancellationToken ct = directory.Token;
            item.IsLoading = true;
            try
            {
                FileEntry[] entries = await client.ListAsync(item.Path, ct).ConfigureAwait(true);
                ct.ThrowIfCancellationRequested();
                if (_client != client || _directory != directory) return;
                foreach (FileEntry entry in entries)
                {
                    item.Children.Add(new FileBrowserItem(entry, ChildPath(item.Path, entry.Name), item.Depth + 1));
                }
                item.ChildrenLoaded = true;
            }
            finally
            {
                item.IsLoading = false;
            }
        }

        item.IsExpanded = true;
        RebuildVisibleFiles();
    }

    private string SelectedFilePath => Selection.SelectedItem?.Path ??
        throw new InvalidOperationException("请选择要操作的文件。");

    private static string ChildPath(string directory, string name)
    {
        if (string.IsNullOrEmpty(name) || name is "." or ".." || name.Contains('/') || name.Contains('\0'))
        {
            throw new IOException("设备返回了无效文件名。");
        }

        return $"{directory.TrimEnd('/')}/{name}";
    }
}
