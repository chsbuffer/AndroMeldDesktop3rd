using System.Collections.ObjectModel;

using Andromeld.Protocol;

using Avalonia.Controls.Selection;
using Avalonia.Threading;

using ReactiveUI;
using ReactiveUI.Primitives;
using ReactiveUI.Primitives.Disposables;
using ReactiveUI.SourceGenerators;

namespace Andromeld.Desktop.ViewModels;

/// <summary>UI-thread-owned file browser state; connection lifecycle is attached by MainViewModel.</summary>
public sealed partial class FileBrowserViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly MultipleDisposable _commands = new();
    private readonly IObservable<bool> _canUseFiles, _canGoUp, _canGoBack, _canGoForward;
    private readonly List<FileBrowserItem> _fileItems = [];
    private readonly List<string> _fileHistory = [];
    private FileBrowserItem[] _selectedItems = [];
    private AndromeldClient? _client;
    private CancellationTokenSource _connection = new();
    private CancellationTokenSource? _directory;
    private CancellationTokenSource? _download;
    private CancellationTokenSource? _upload;
    private Task? _uploadTask;
    private int _fileHistoryIndex = -1;
    private bool _settingRoot;
    private bool _disposed;
    private string _deviceName = "Android";

    public FileBrowserViewModel()
    {
        Selection.Source = VisibleFiles;
        Selection.SelectionChanged += (_, _) => NotifySelectionChanged();
        _canUseFiles = this.WhenAnyValue(x => x.CanUseFiles);
        _canGoUp = this.WhenAnyValue(x => x.CanGoUp);
        _canGoBack = this.WhenAnyValue(x => x.CanGoBack);
        _canGoForward = this.WhenAnyValue(x => x.CanGoForward);
        this.WhenAnyValue(x => x.SelectedRoot).Subscribe(root =>
        {
            ResetFileHistory();
            if (!_settingRoot && root is not null && CanUseFiles)
            {
                _ = NavigateSafelyAsync(root.Path);
            }
        }).DisposeWith(_commands);
        this.WhenAnyValue(x => x.ViewMode).Subscribe(_ => RebuildVisibleFiles()).DisposeWith(_commands);
        foreach (ReactiveCommand<RxVoid, RxVoid> command in new[]
                 {
                     GoCommand, RefreshCommand, UpCommand, BackCommand, ForwardCommand, CancelDownloadCommand, CancelUploadCommand,
                     DismissErrorCommand, ShowDetailsCommand, ShowIconsCommand, ShowTreeCommand,
                     SortByNameCommand, SortBySizeCommand, SortByModifiedCommand, ChooseNameSortCommand, ChooseSizeSortCommand,
                     ChooseModifiedSortCommand, SortAscendingCommand, SortDescendingCommand, SelectAllCommand
                 })
        {
            command.ThrownExceptions.Subscribe(ex =>
            {
                if (ex is not OperationCanceledException) Report(ex);
            }).DisposeWith(_commands);
            _commands.Add(command);
        }
    }

    public ObservableCollection<FileBrowserItem> VisibleFiles { get; } = [];
    public ObservableCollection<FileBreadcrumb> FileBreadcrumbs { get; } = [];
    public ObservableCollection<StorageRoot> Roots { get; } = [];
    public SelectionModel<FileBrowserItem> Selection { get; } = new() { SingleSelect = false };

    [Reactive(nameof(CanUseFiles), nameof(ConnectionLabel), nameof(CanTransferFiles), nameof(CanCreateFolder))]
    public partial bool IsAttached { get; private set; }

    [Reactive(nameof(CanCreateFolder))]
    public partial string CurrentPath { get; private set; } = "";

    [Reactive]
    public partial string PathInput { get; set; } = "";

    [Reactive(nameof(CanGoUp))]
    public partial StorageRoot? SelectedRoot { get; set; }

    [Reactive(nameof(CanGoUp), nameof(CanGoBack), nameof(CanGoForward), nameof(FilesEmpty), nameof(CanTransferFiles),
        nameof(CanDownloadSelection), nameof(CanDeleteSelection), nameof(CanCreateFolder))]
    public partial bool FilesLoading { get; private set; }

    [Reactive(nameof(CanTransferFiles), nameof(CanDownloadSelection), nameof(CanDeleteSelection), nameof(TransferStatus),
        nameof(HasTransferStatus), nameof(CanCreateFolder))]
    public partial bool Downloading { get; private set; }

    [Reactive(nameof(CanDeleteSelection), nameof(TransferStatus), nameof(HasTransferStatus), nameof(CanCreateFolder))]
    public partial bool Uploading { get; private set; }

    [Reactive(nameof(CanCreateFolder), nameof(CanTransferFiles), nameof(CanDownloadSelection), nameof(CanDeleteSelection))]
    public partial bool CreatingFolder { get; private set; }

    [Reactive]
    public partial bool IsEditingFilePath { get; set; }

    [Reactive(nameof(TransferStatus), nameof(HasTransferStatus))]
    public partial bool FileDragPreparing { get; set; }

    [Reactive(nameof(TransferStatus), nameof(HasTransferStatus))]
    public partial string FileDragStatus { get; set; } = "";

    [Reactive]
    public partial string FilesStatus { get; internal set; } = "连接后浏览手机存储空间";

    [Reactive(nameof(TransferStatus), nameof(HasTransferStatus))]
    public partial string DownloadStatus { get; private set; } = "";

    [Reactive(nameof(TransferStatus), nameof(HasTransferStatus))]
    public partial string UploadStatus { get; private set; } = "";

    [Reactive(nameof(HasError))]
    public partial string Error { get; private set; } = "";

    public bool HasError => Error.Length > 0;
    public bool CanUseFiles => IsAttached;
    public bool CanTransferFiles => IsAttached && !Downloading && !FilesLoading && !CreatingFolder && _client is not null && _client.Capabilities.Contains("file");
    public bool CanCreateFolder => IsAttached && CurrentPath.Length > 0 && !FilesLoading && !Downloading && !Uploading && !CreatingFolder &&
                                   _client is not null && _client.Capabilities.Contains("fs");
    public bool CanGoUp => CanUseFiles && !FilesLoading && SelectedRoot is not null &&
                           !string.Equals(CurrentPath, SelectedRoot.Path, StringComparison.Ordinal);
    public bool CanGoBack => CanUseFiles && !FilesLoading && _fileHistoryIndex > 0;
    public bool CanGoForward => CanUseFiles && !FilesLoading && _fileHistoryIndex >= 0 && _fileHistoryIndex + 1 < _fileHistory.Count;
    public bool FilesEmpty => !FilesLoading && VisibleFiles.Count == 0;
    public string ConnectionLabel => IsAttached ? _deviceName : "未连接";
    public FileBrowserItem? SelectedItem => Selection.SelectedItem;
    public IReadOnlyList<FileBrowserItem> SelectedItems => _selectedItems;
    public int SelectionCount => Selection.Count;
    public bool HasSelection => Selection.Count > 0;
    public bool HasSelectedFiles => SelectionCount > 0 && SelectedItems.All(static item => !item.IsDir);
    public bool CanDownloadSelection => HasSelectedFiles && CanTransferFiles;
    public bool CanDeleteSelection => HasSelection && CanUseFiles && !FilesLoading && !Downloading && !Uploading && !CreatingFolder;
    public string TransferStatus
    {
        get
        {
            if (FileDragPreparing && FileDragStatus.Length > 0) return FileDragStatus;
            if (Downloading && DownloadStatus.Length > 0) return DownloadStatus;
            if (Uploading && UploadStatus.Length > 0) return UploadStatus;
            if (FileDragStatus.Length > 0) return FileDragStatus;
            if (DownloadStatus.Length > 0) return DownloadStatus;
            return UploadStatus;
        }
    }

    public bool HasTransferStatus => !string.IsNullOrWhiteSpace(TransferStatus);

    public async Task AttachAsync(AndromeldClient client, string deviceName, CancellationToken connectionToken)
    {
        Dispatcher.UIThread.VerifyAccess();
        ArgumentNullException.ThrowIfNull(client);
        _connection.Dispose();
        _connection = CancellationTokenSource.CreateLinkedTokenSource(connectionToken);
        _client = client;
        _deviceName = deviceName;
        Error = string.Empty;
        IsAttached = true;
        await LoadRootsAsync().ConfigureAwait(true);
    }

    public async Task DetachAsync()
    {
        Dispatcher.UIThread.VerifyAccess();
        IsAttached = false;
        _client = null;
        _deviceName = "Android";
        CancelOperations();
        await StopUploadsAsync().ConfigureAwait(true);
        await _connection.CancelAsync().ConfigureAwait(true);
        _connection.Dispose();
        _connection = new CancellationTokenSource();
        _fileItems.Clear();
        VisibleFiles.Clear();
        FileBreadcrumbs.Clear();
        Roots.Clear();
        SelectedRoot = null;
        Selection.Clear();
        ResetFileHistory();
        CurrentPath = string.Empty;
        PathInput = string.Empty;
        FilesStatus = "连接后浏览手机存储空间";
        DownloadStatus = string.Empty;
        UploadStatus = string.Empty;
        FileDragStatus = string.Empty;
        FileDragPreparing = false;
        FilesLoading = false;
        Downloading = false;
        Uploading = false;
        Error = string.Empty;
        this.RaisePropertyChanged(nameof(FilesEmpty));
    }

    public void Suspend()
    {
        Dispatcher.UIThread.VerifyAccess();
        _directory?.Cancel();
        _download?.Cancel();
        _upload?.Cancel();
        FileDragPreparing = false;
        FilesLoading = false;
        this.RaisePropertyChanged(nameof(FilesEmpty));
    }

    public void Resume()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!CanUseFiles || _directory is { IsCancellationRequested: false }) return;
        _directory?.Dispose();
        _directory = CancellationTokenSource.CreateLinkedTokenSource(_connection.Token);
        this.RaisePropertyChanged(nameof(FilesEmpty));
    }

    public void CancelOperations()
    {
        Dispatcher.UIThread.VerifyAccess();
        _upload?.Cancel();
        _directory?.Cancel();
        _download?.Cancel();
    }

    public void Report(Exception ex)
    {
        Error = ex switch
        {
            UnauthorizedAccessException => "没有权限执行该文件操作。",
            TimeoutException => "设备响应超时，请检查手机上的 AndroMeld 服务是否仍在运行。",
            _ => ex.Message
        };
    }

    [ReactiveCommand]
    private void DismissError() => Error = string.Empty;

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await DetachAsync().ConfigureAwait(true);
        _connection.Dispose();
        _commands.Dispose();
    }
}
