using System.Collections.Specialized;

using Andromeld.Desktop.Services;
using Andromeld.Desktop.ViewModels;
using Andromeld.Protocol;

using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Selection;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;

using FluentAvalonia.UI.Controls;

namespace Andromeld.Desktop.Views;

/// <summary>UI-thread owned; closes only its file operations, never the shared device connection.</summary>
public partial class FileBrowserWindow : Window
{
    private readonly CancellationTokenSource _dragLifetime = new();
    private INotifyCollectionChanged? _breadcrumbs;
    private bool _closed;
    private Point _dragOrigin;
    private PointerPressedEventArgs? _dragPointer;
    private FileBrowserItem? _dragItem;
    private FileBrowserItem? _deferredSelectionItem;
    private bool _dragging;
    private PointerPressedEventArgs? _marqueePointer;
    private Point _marqueeStart;
    private FileBrowserItem[] _marqueeBase = [];
    private bool _marqueeActive;
    private FAContentDialog? _createFolderDialog;

    public FileBrowserWindow()
    {
        InitializeComponent();
        DesktopPlatform.ConfigureWindow(this, "Files");
        FileList.DoubleTapped += FileDoubleTapped;
        FileList.AddHandler(KeyDownEvent, FileKeyDown, RoutingStrategies.Bubble, handledEventsToo: true);
        PathBox.KeyDown += PathKeyDown;
        AddHandler(KeyDownEvent, AddressKeyDown, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, MouseNavigationPressed, RoutingStrategies.Tunnel);
        DownloadButton.Click += DownloadClick;
        DragDrop.SetAllowDrop(FileList, true);
        FileList.AddHandler(DragDrop.DragOverEvent, DragOver);
        FileList.AddHandler(DragDrop.DropEvent, DropFiles);
        FileList.AddHandler(PointerPressedEvent, FilePressed, RoutingStrategies.Tunnel);
        FileList.AddHandler(PointerMovedEvent, FileMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        FileList.AddHandler(PointerReleasedEvent, FileReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        FileList.AddHandler(PointerCaptureLostEvent, FileCaptureLost, RoutingStrategies.Tunnel, handledEventsToo: true);
        DataContextChanged += BreadcrumbContextChanged;
        BreadcrumbScroller.SizeChanged += (_, _) => ScrollBreadcrumbsToEnd();
    }

    private FileBrowserViewModel? Model => DataContext as FileBrowserViewModel;

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Model?.Resume();
        ScrollBreadcrumbsToEnd();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (!e.Cancel)
        {
            DesktopPlatform.ClearWindowIdentity(this);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _createFolderDialog?.Hide();
        if (_breadcrumbs is not null) _breadcrumbs.CollectionChanged -= BreadcrumbsChanged;
        _breadcrumbs = null;
        Model?.Suspend();
        _dragLifetime.Cancel();
        _dragLifetime.Dispose();
        base.OnClosed(e);
    }

    private void BreadcrumbContextChanged(object? sender, EventArgs e)
    {
        if (_breadcrumbs is not null) _breadcrumbs.CollectionChanged -= BreadcrumbsChanged;
        _breadcrumbs = Model?.FileBreadcrumbs;
        if (_breadcrumbs is not null) _breadcrumbs.CollectionChanged += BreadcrumbsChanged;
        ScrollBreadcrumbsToEnd();
    }

    private void BreadcrumbsChanged(object? sender, NotifyCollectionChangedEventArgs e) => ScrollBreadcrumbsToEnd();

    private void ScrollBreadcrumbsToEnd()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_closed) return;
            ScrollViewer scroller = BreadcrumbScroller;
            scroller.Offset = new Vector(Math.Max(0, scroller.Extent.Width - scroller.Viewport.Width), 0);
        }, DispatcherPriority.Loaded);
    }

    private void DragOver(object? sender, DragEventArgs e)
    {
        FileBrowserViewModel? model = Model;
        e.DragEffects = model is { CanUseFiles: true, Uploading: false } && !_dragging && e.DataTransfer.Contains(DataFormat.File)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void DropFiles(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_closed || Model is not { CanUseFiles: true, Uploading: false } model || _dragging) return;
        try
        {
            IStorageItem[] files = e.DataTransfer.TryGetFiles()?.ToArray() ?? [];
            string[] paths = files.Select(static file => file.TryGetLocalPath() ?? throw new IOException("请拖入本地文件。")).ToArray();
            await model.UploadFilesAsync(paths);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { model.Report(ex); }
    }

    private void FilePressed(object? sender, PointerPressedEventArgs e)
    {
        if (_closed) return;
        var source = e.Source as Visual;
        bool left = e.GetCurrentPoint(FileList).Properties.IsLeftButtonPressed;
        if (!left) return;
        _dragOrigin = e.GetPosition(FileList);
        if (source?.FindAncestorOfType<Button>(includeSelf: true) is not null) return;
        if (source?.FindAncestorOfType<ScrollBar>(includeSelf: true) is not null) return;

        _dragItem = source?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as FileBrowserItem;
        if (_dragItem is null)
        {
            BeginMarquee(e);
            return;
        }

        FileBrowserViewModel? model = Model;
        if (model is null) return;
        bool additive = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift)) != KeyModifiers.None;
        if (!additive && model.Selection.Count > 1 && model.SelectedItems.Contains(_dragItem))
        {
            // A plain click on one of several selected rows must not collapse the
            // selection before the drag threshold is reached.
            _deferredSelectionItem = _dragItem;
            _dragPointer = e;
            e.Handled = true;
            FileList.Focus();
            return;
        }

        _dragPointer = e;
    }

    private void BeginMarquee(PointerPressedEventArgs e)
    {
        FileBrowserViewModel? model = Model;
        if (model is null) return;
        bool additive = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift)) != KeyModifiers.None;
        _marqueePointer = e;
        _marqueeStart = e.GetPosition(FileList);
        _marqueeBase = additive ? model.SelectedItems.ToArray() : [];
        _marqueeActive = true;
        _dragItem = null;
        _dragPointer = null;
        _deferredSelectionItem = null;
        SelectionBox.IsVisible = false;
        e.Pointer.Capture(FileList);
        FileList.Focus();
        if (!additive)
        {
            model.Selection.Clear();
        }
        e.Handled = true;
    }

    private async void FileMoved(object? sender, PointerEventArgs e)
    {
        if (_marqueeActive && _marqueePointer is not null)
        {
            UpdateMarquee(e);
            e.Handled = true;
            return;
        }

        if (_dragPointer is not { } press || _dragging || !e.GetCurrentPoint(FileList).Properties.IsLeftButtonPressed ||
            Math.Abs(e.GetPosition(FileList).X - _dragOrigin.X) + Math.Abs(e.GetPosition(FileList).Y - _dragOrigin.Y) < 6 ||
            Model is not { CanTransferFiles: true } model)
        {
            return;
        }

        FileBrowserItem[] entries = model.SelectedItems.ToArray();
        if (_dragItem is { } origin && !entries.Contains(origin))
        {
            int index = model.VisibleFiles.IndexOf(origin);
            if (index >= 0)
            {
                model.Selection.Clear();
                model.Selection.Select(index);
            }
            entries = [origin];
        }
        if (entries.Length == 0) return;
        if (Array.Exists(entries, static item => item.IsDir))
        {
            _dragPointer = null;
            model.FileDragStatus = "请选择普通文件拖出；暂不支持拖出整个文件夹。";
            return;
        }

        _dragPointer = null;
        _deferredSelectionItem = null;
        _dragging = true;
        e.Handled = true;
        e.Pointer.Capture(null);
        try
        {
            model.FileDragStatus = "将所选文件拖到目标位置以复制";
            var progress = new Progress<string>(message =>
            {
                if (_closed || !_dragging) return;
                model.FileDragPreparing = true;
                model.FileDragStatus = message;
            });
            await using FileDragBatch batch = model.CreateFileBatch(entries, _dragLifetime.Token, progress: progress);
            bool copied = await batch.RunAsync(this, press);
            if (!_closed) model.FileDragStatus = copied ? $"已拖出 {entries.Length} 个文件" : "拖动已取消";
        }
        catch (OperationCanceledException)
        {
            if (!_closed) model.FileDragStatus = "拖动已取消";
        }
        catch (Exception ex)
        {
            if (!_closed)
            {
                model.FileDragStatus = "拖出失败";
                model.Report(ex);
            }
        }
        finally
        {
            _dragging = false;
            if (!_closed) model.FileDragPreparing = false;
        }
    }

    private void UpdateMarquee(PointerEventArgs e)
    {
        FileBrowserViewModel? model = Model;
        if (model is null || _marqueePointer is null) return;
        Point current = e.GetPosition(FileList);
        Rect rectangle = new(
            Math.Min(current.X, _marqueeStart.X), Math.Min(current.Y, _marqueeStart.Y),
            Math.Abs(current.X - _marqueeStart.X), Math.Abs(current.Y - _marqueeStart.Y));
        if (rectangle.Width < 3 && rectangle.Height < 3) return;

        Canvas.SetLeft(SelectionBox, rectangle.X);
        Canvas.SetTop(SelectionBox, rectangle.Y);
        SelectionBox.Width = rectangle.Width;
        SelectionBox.Height = rectangle.Height;
        SelectionBox.IsVisible = true;
        List<FileBrowserItem> selected = [.. _marqueeBase];
        foreach (Control container in FileList.GetRealizedContainers())
        {
            if (container.DataContext is not FileBrowserItem item || !container.IsVisible) continue;
            Point? origin = container.TranslatePoint(new Point(0, 0), FileList);
            if (origin is null || !rectangle.Intersects(new Rect(origin.Value, container.Bounds.Size))) continue;
            if (!selected.Contains(item)) selected.Add(item);
        }

        using (model.Selection.BatchUpdate())
        {
            model.Selection.Clear();
            foreach (FileBrowserItem item in selected)
            {
                int index = model.VisibleFiles.IndexOf(item);
                if (index >= 0) model.Selection.Select(index);
            }
        }
    }

    private void FileReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_marqueeActive)
        {
            EndMarquee(e.Pointer);
        }

        if (!_dragging && _deferredSelectionItem is { } deferred && Model is { } model)
        {
            int index = model.VisibleFiles.IndexOf(deferred);
            using (model.Selection.BatchUpdate())
            {
                model.Selection.Clear();
                if (index >= 0) model.Selection.Select(index);
            }
        }

        _dragPointer = null;
        _deferredSelectionItem = null;
        _dragItem = null;
    }

    private void FileCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_marqueeActive) EndMarquee(null);
        _dragPointer = null;
        _deferredSelectionItem = null;
        _dragItem = null;
    }

    private void EndMarquee(IPointer? pointer)
    {
        _marqueeActive = false;
        _marqueePointer = null;
        _marqueeBase = [];
        SelectionBox.IsVisible = false;
        pointer?.Capture(null);
    }

    private async void FileDoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((e.Source as Visual)?.FindAncestorOfType<Button>(includeSelf: true) is not null || Model is not { } model) return;
        await OpenSelectedAsync(model);
    }

    private async void FileKeyDown(object? sender, KeyEventArgs e)
    {
        if (Model is not { } model) return;
        if (e.Key == Key.A && e.KeyModifiers == KeyModifiers.Control)
        {
            e.Handled = true;
            model.SelectAllVisible();
            return;
        }
        if (e.Key == Key.F5)
        {
            e.Handled = true;
            try { await model.RefreshAsync(); }
            catch (OperationCanceledException) { /* Navigation or window close. */ }
            catch (Exception ex) { model.Report(ex); }
            return;
        }
        if (e.Key == Key.Back)
        {
            e.Handled = true;
            try { await model.GoUpAsync(); }
            catch (OperationCanceledException) { /* Navigation or window close. */ }
            catch (Exception ex) { model.Report(ex); }
            return;
        }
        if ((e.Key is Key.Left or Key.Right) && (e.KeyModifiers & KeyModifiers.Alt) != KeyModifiers.None)
        {
            e.Handled = true;
            try
            {
                if (e.Key == Key.Left) await model.GoBackAsync();
                else await model.GoForwardAsync();
            }
            catch (OperationCanceledException) { /* Navigation or window close. */ }
            catch (Exception ex) { model.Report(ex); }
            return;
        }
        if (e.Key == Key.Delete)
        {
            e.Handled = true;
            await DeleteSelectionWithConfirmationAsync(model);
            return;
        }
        if (model.IsTreeView && e.Key is Key.Right or Key.Left && model.SelectedItem is { } item)
        {
            e.Handled = true;
            if (item.IsDir && (e.Key == Key.Right && !item.IsExpanded || e.Key == Key.Left && item.IsExpanded))
            {
                await ToggleDirectoryAsync(model, item);
            }
            return;
        }
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await OpenSelectedAsync(model);
    }

    private static async Task OpenSelectedAsync(FileBrowserViewModel model)
    {
        try { await model.OpenSelectedAsync(); }
        catch (OperationCanceledException) { /* Navigation or window close. */ }
        catch (Exception ex) { model.Report(ex); }
    }

    private async void PathKeyDown(object? sender, KeyEventArgs e)
    {
        if (Model is not { } model) return;
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            model.PathInput = model.CurrentPath;
            model.IsEditingFilePath = false;
            FileList.Focus();
            return;
        }
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        try { await model.NavigateAsync(model.PathInput); }
        catch (OperationCanceledException) { /* Navigation or window close. */ }
        catch (Exception ex) { model.Report(ex); }
    }

    private void AddressKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not FileBrowserViewModel || !(e.Key == Key.F4 || e.Key == Key.L && e.KeyModifiers == KeyModifiers.Control)) return;
        e.Handled = true;
        EditPath();
    }

    private async void MouseNavigationPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_closed || Model is not { } model) return;
        PointerPointProperties properties = e.GetCurrentPoint(this).Properties;
        bool back = properties.PointerUpdateKind == PointerUpdateKind.XButton1Pressed || properties.IsXButton1Pressed;
        bool forward = properties.PointerUpdateKind == PointerUpdateKind.XButton2Pressed || properties.IsXButton2Pressed;
        if (!back && !forward) return;
        e.Handled = true;
        try
        {
            if (back) await model.GoBackAsync();
            else await model.GoForwardAsync();
        }
        catch (OperationCanceledException) { /* Navigation or window close. */ }
        catch (Exception ex) { model.Report(ex); }
    }

    private void EditPathClick(object? sender, RoutedEventArgs e) => EditPath();

    private void SortHeaderClick(object? sender, RoutedEventArgs e) => FileList.Focus();

    private void EditPath()
    {
        if (Model is not { } model) return;
        model.PathInput = model.CurrentPath;
        model.IsEditingFilePath = true;
        PathBox.Focus();
        PathBox.SelectAll();
    }

    private async void BreadcrumbClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not FileBreadcrumb crumb || Model is not { } model) return;
        try { await model.NavigateAsync(crumb.Path); }
        catch (OperationCanceledException) { /* Navigation or window close. */ }
        catch (Exception ex) { model.Report(ex); }
    }

    private async void ExpandDirectory(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as Control)?.DataContext is FileBrowserItem item && Model is { } model)
        {
            await ToggleDirectoryAsync(model, item);
        }
    }

    private static async Task ToggleDirectoryAsync(FileBrowserViewModel model, FileBrowserItem item)
    {
        try { await model.ToggleFileDirectoryAsync(item); }
        catch (OperationCanceledException) { /* Navigation or window close. */ }
        catch (Exception ex) { model.Report(ex); }
    }

    private async void CreateFolderClick(object? sender, RoutedEventArgs e)
    {
        if (_closed || _createFolderDialog is not null || Model is not { CanCreateFolder: true } model) return;
        var nameBox = new TextBox { Text = "新建文件夹", PlaceholderText = "文件夹名称" };
        AutomationProperties.SetName(nameBox, "文件夹名称");
        var validation = new TextBlock
        {
            Text = "名称过长或包含无效字符。请勿使用 /、\\、控制字符、首尾空白、. 或 ..。",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            IsVisible = false,
            Classes = { "muted" }
        };
        var dialog = new FAContentDialog
        {
            Title = "新建文件夹",
            Content = new StackPanel { Spacing = 8, Children = { nameBox, validation } },
            PrimaryButtonText = "创建",
            CloseButtonText = "取消",
            DefaultButton = FAContentDialogButton.Primary
        };
        nameBox.TextChanged += (_, _) =>
        {
            dialog.IsPrimaryButtonEnabled = FileNameRules.IsValid(nameBox.Text);
            validation.IsVisible = !dialog.IsPrimaryButtonEnabled;
        };
        dialog.Opened += (_, _) => { nameBox.Focus(); nameBox.SelectAll(); };
        void StateChanged(object? source, System.ComponentModel.PropertyChangedEventArgs args)
        {
            if (!model.CanCreateFolder) dialog.Hide();
        }

        _createFolderDialog = dialog;
        model.PropertyChanged += StateChanged;
        try
        {
            if (await dialog.ShowAsync(this) != FAContentDialogResult.Primary || _closed) return;
            await model.CreateFolderAsync(nameBox.Text ?? string.Empty, _dragLifetime.Token);
        }
        catch (OperationCanceledException) { /* Navigation, disconnect or window close. */ }
        catch (Exception ex) { if (!_closed) model.Report(ex); }
        finally
        {
            model.PropertyChanged -= StateChanged;
            _createFolderDialog = null;
        }
    }

    private async void DownloadClick(object? sender, RoutedEventArgs e) => await DownloadSelectionAsync();

    private async Task DownloadSelectionAsync()
    {
        if (Model is not { } model || !model.CanDownloadSelection) return;
        FileBrowserItem[] items = model.SelectedItems.Where(static item => !item.IsDir).ToArray();
        if (items.Length == 0) return;
        try
        {
            if (items.Length == 1)
            {
                using IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "下载手机文件", SuggestedFileName = items[0].Name, ShowOverwritePrompt = true
                });
                if (file is null || _closed) return;
                string path = file.TryGetLocalPath() ?? throw new IOException("请选择本地文件夹保存文件。");
                await model.DownloadAsync(items[0], path);
                return;
            }

            IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "下载到文件夹", AllowMultiple = false
            });
            IStorageFolder? folder = folders.Count > 0 ? folders[0] : null;
            if (folder is null || _closed) return;
            string directory = folder.TryGetLocalPath() ?? throw new IOException("请选择本地文件夹。");
            await model.DownloadManyAsync(items, directory, _dragLifetime.Token);
        }
        catch (OperationCanceledException) { /* User cancellation. */ }
        catch (Exception ex) { model.Report(ex); }
    }

    private async void ContextDownloadClick(object? sender, RoutedEventArgs e) => await DownloadSelectionAsync();

    private async void ContextCopyPathClick(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model || !model.HasSelection) return;
        try
        {
            string text = string.Join('\n', model.SelectedItems.Select(static item => item.Path));
            IClipboard clipboard = Clipboard ?? throw new IOException("系统剪贴板不可用。");
            await clipboard.SetTextAsync(text);
            model.FilesStatus = $"已复制 {model.SelectionCount} 个路径";
        }
        catch (Exception ex) { model.Report(ex); }
    }

    private async void ContextDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (Model is { } model) await DeleteSelectionWithConfirmationAsync(model);
    }

    private async Task DeleteSelectionWithConfirmationAsync(FileBrowserViewModel model)
    {
        if (!model.CanDeleteSelection) return;
        bool hasFolder = model.SelectedItems.Any(static item => item.IsDir);
        var dialog = new FAContentDialog
        {
            Title = "永久删除",
            Content = hasFolder
                ? $"确定永久删除选中的 {model.SelectionCount} 项？文件夹及其内容会一并删除，无法恢复。"
                : $"确定永久删除选中的 {model.SelectionCount} 个文件？此操作无法恢复。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = FAContentDialogButton.Close
        };
        try
        {
            if (await dialog.ShowAsync(this) != FAContentDialogResult.Primary || _closed) return;
            await model.DeleteSelectionAsync(_dragLifetime.Token);
        }
        catch (OperationCanceledException) { /* Window close. */ }
        catch (Exception ex) { model.Report(ex); }
    }
}
