using System.Text;

using Andromeld.Desktop.Controls;
using Andromeld.Desktop.Services;
using Andromeld.Desktop.ViewModels;
using Andromeld.Desktop.Views;
using Andromeld.Protocol;
using Andromeld.Testing;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;

using Lucide.Avalonia;

using ReactiveUI.Primitives;

namespace Andromeld.Desktop.Tests;

internal static class FileBrowserChecks
{
    internal static async Task RunAsync(FileBrowserViewModel files, FileBrowserWindow window, MockPhone phone, string temporary, CancellationToken ct)
    {
        var sparse = new AdaptiveTilePanel { MinimumItemWidth = 100, Spacing = 10 };
        sparse.Children.Add(new Border());
        sparse.Children.Add(new Border());
        sparse.Measure(new Size(800, 400));
        sparse.Arrange(new Rect(0, 0, 800, 400));
        Program.Check(sparse.Children.All(tile => tile.Bounds.Width is >= 100 and < 200),
            "adaptive tiles keep their width instead of stretching sparse rows across the viewport");

        const string root = "/storage/emulated/0", downloads = root + "/Download", nested = downloads + "/子目录";
        phone.FileListings[downloads] = [new("子目录", true, 0, 0, null), new("denied", true, 0, 0, null), new("同名 #%.txt", false, MockPhone.Payload.Length, 0, "text/plain")];
        phone.FileListings[nested] = [new("同名 #%.txt", false, MockPhone.Payload.Length, 0, "text/plain")];
        var list = window.FindControl<ListBox>("FileList")!;
        Program.Check(files.ViewMode == FileBrowserViewMode.Details && files.VisibleFiles.Count == 201 &&
            files.FileBreadcrumbs.Single().Path == root, "file browser starts in details view at the storage-root breadcrumb");
        await files.ShowIconsCommand.Execute().ToTask();
        window.UpdateLayout();
        Program.Check(files.IsIconView && list.Classes.Contains("icons") && list.GetVisualDescendants().OfType<AdaptiveTilePanel>().Any(),
            "icon view lays out selectable files in a keyboard-navigable wrapping grid");
        Program.Check(window.FindControl<RadioButton>("IconsViewButton")!.IsChecked == true &&
            window.FindControl<RadioButton>("DetailsViewButton")!.IsChecked == false, "view mode uses mutually exclusive icon toggles");
        await files.ShowTreeCommand.Execute().ToTask();
        var folder = files.VisibleFiles.First(item => item.IsDir);
        var requests = phone.FileListRequests.Count;
        await files.ToggleFileDirectoryAsync(folder);
        Program.Check(files.CurrentPath == root && folder.IsExpanded && files.VisibleFiles.Any(item => item.Path == nested) &&
            phone.FileListRequests.Count == requests + 1, "tree expansion loads a child directory without navigating away from its parent");
        var subfolder = folder.Children.Single(item => item.IsDir && item.Name == "子目录");
        await files.ToggleFileDirectoryAsync(subfolder);
        var child = subfolder.Children.Single();
        Select(files, child);
        var saved = Path.Combine(temporary, "tree-download.bin");
        await files.DownloadAsync(child, saved);
        Program.Check(phone.DownloadPaths.Last() == nested + "/同名 #%.txt" && File.ReadAllBytes(saved).SequenceEqual(MockPhone.Payload),
            "downloading an expanded descendant uses its full path, including Unicode and URI punctuation");

        // A selection can contain identical names in different expanded directories.
        string treeTarget = Path.Combine(temporary, "tree-drag");
        Directory.CreateDirectory(treeTarget);
        await using (var batch = files.CreateFileBatch([folder.Children.Single(item => !item.IsDir), child], ct))
        {
            await batch.CopyToDirectoryAsync(treeTarget);
        }
        string[] treeFiles = Directory.GetFiles(treeTarget);
        Program.Check(treeFiles.Length == 2 && treeFiles.Select(Path.GetFileName).Distinct(StringComparer.Ordinal).Count() == 2 &&
            phone.DownloadPaths.TakeLast(2).SequenceEqual([downloads + "/同名 #%.txt", nested + "/同名 #%.txt"]),
            "tree drag snapshots each full remote path and assigns distinct local names to duplicate file names");
        foreach (string file in treeFiles)
            Program.Check(File.ReadAllBytes(file).SequenceEqual(MockPhone.Payload), "a tree drag writes each complete file to its destination");

        await files.ToggleFileDirectoryAsync(folder);
        Program.Check(!files.VisibleFiles.Contains(child) && files.SelectedItems.Count == 0, "collapsing an ancestor clears the hidden descendant selection");
        requests = phone.FileListRequests.Count;
        await files.ToggleFileDirectoryAsync(folder);
        Program.Check(phone.FileListRequests.Count == requests, "reopening a tree branch reuses its already loaded children");
        var denied = folder.Children.Single(item => item.Name == "denied");
        try { await files.ToggleFileDirectoryAsync(denied); throw new Exception("Denied tree directory accepted"); }
        catch (IOException) { Program.Check(!denied.IsExpanded && !denied.IsLoading && files.CurrentPath == root, "a denied tree branch restores its expander without changing the address"); }

        Select(files, subfolder);
        await files.OpenSelectedAsync();
        Program.Check(files.CurrentPath == nested && files.FileBreadcrumbs.Select(item => item.Path).SequenceEqual([root, downloads, nested]),
            "opening a tree directory updates every breadcrumb with its exact ancestor path");
        Program.Check(files.CanGoBack && !files.CanGoForward, "file history records the current path and disables forward at the newest entry");
        await files.GoBackAsync();
        Program.Check(files.CurrentPath == root && !files.CanGoBack && files.CanGoForward,
            "navigating back moves to the previous directory and enables forward");
        await files.GoForwardAsync();
        Program.Check(files.CurrentPath == nested && files.CanGoBack && !files.CanGoForward,
            "navigating forward restores the newer directory without appending a duplicate history entry");

        window.UpdateLayout();
        var upButton = window.FindControl<Button>("UpButton")!;
        var backButton = window.FindControl<Button>("BackButton")!;
        var forwardButton = window.FindControl<Button>("ForwardButton")!;
        Program.Check(upButton.Bounds.X < backButton.Bounds.X && backButton.Bounds.X < forwardButton.Bounds.X,
            "toolbar back and forward buttons sit immediately to the right of Up");
        Program.Check(backButton.IsEnabled && !forwardButton.IsEnabled, "toolbar back is enabled and forward is disabled at the newest history entry");
        backButton.Command!.Execute(null);
        await UntilAsync(() => !files.FilesLoading && files.CurrentPath == root, ct);
        Program.Check(files.CurrentPath == root && !backButton.IsEnabled && forwardButton.IsEnabled,
            "toolbar back navigates to the previous directory and enables forward");
        forwardButton.Command!.Execute(null);
        await UntilAsync(() => !files.FilesLoading && files.CurrentPath == nested, ct);
        Program.Check(files.CurrentPath == nested && backButton.IsEnabled && !forwardButton.IsEnabled,
            "toolbar forward navigates to the newer directory and disables forward");

        // Headless input injection cannot run against the native Wayland backend.
        if (string.Equals(((TopLevel)window).PlatformImpl?.GetType().Namespace, "Avalonia.Headless", StringComparison.Ordinal))
        {
            window.UpdateLayout();
            var sidePoint = list.TranslatePoint(new Point(20, 20), window) ?? new Point(20, 20);
            window.MouseDown(sidePoint, MouseButton.XButton1, RawInputModifiers.None);
            window.MouseUp(sidePoint, MouseButton.XButton1, RawInputModifiers.None);
            await UntilAsync(() => !files.FilesLoading && files.CurrentPath == root, ct);
            Program.Check(files.CurrentPath == root && files.CanGoForward, "mouse XButton1 navigates the file browser back");
            window.MouseDown(sidePoint, MouseButton.XButton2, RawInputModifiers.None);
            window.MouseUp(sidePoint, MouseButton.XButton2, RawInputModifiers.None);
            await UntilAsync(() => !files.FilesLoading && files.CurrentPath == nested, ct);
            Program.Check(files.CurrentPath == nested && !files.CanGoForward, "mouse XButton2 navigates the file browser forward");
        }

        window.UpdateLayout();
        var rootCrumb = window.FindControl<ItemsControl>("Breadcrumbs")!.GetVisualDescendants().OfType<Button>()
            .Single(button => button.DataContext is FileBreadcrumb crumb && crumb.Path == root);
        rootCrumb.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => !files.FilesLoading && files.CurrentPath == root, ct);
        Program.Check(files.FileBreadcrumbs.Count == 1, "clicking a breadcrumb navigates back to the storage root");
        window.RaiseEvent(new KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Key.L, KeyModifiers = KeyModifiers.Control });
        Program.Check(files.IsEditingFilePath && window.FindControl<TextBox>("PathBox")!.Text == root, "Ctrl+L switches breadcrumbs to the editable full path");
        window.FindControl<TextBox>("PathBox")!.RaiseEvent(new KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Key.Escape });
        Program.Check(!files.IsEditingFilePath && files.CurrentPath == root, "Escape leaves path editing without navigating");
        var rootItem = files.VisibleFiles.First(item => !item.IsDir);
        Select(files, rootItem);
        string rootTarget = Path.Combine(temporary, "root-drag");
        Directory.CreateDirectory(rootTarget);
        await using (var batch = files.CreateFileBatch([rootItem], ct))
        {
            await batch.CopyToDirectoryAsync(rootTarget);
        }
        Program.Check(phone.DownloadPaths.Last() == root + "/" + rootItem.Name &&
            File.ReadAllBytes(Directory.GetFiles(rootTarget).Single()).SequenceEqual(MockPhone.Payload),
            "files directly under sdcard root are draggable using their full path");

        await CheckDirectCopyAsync(temporary, ct);

        phone.FileListings[downloads] =
        [
            new FileEntry("first.txt", false, 300, 1000, "text/plain"),
            new FileEntry("second.txt", false, 100, 2000, "text/plain"),
            new FileEntry("folder", true, 0, 3000, null),
            new FileEntry("a very long folder name that wraps onto a second line", true, 0, 4000, null)
        ];
        await files.NavigateAsync(downloads);
        string[] byName = ["a very long folder name that wraps onto a second line", "folder", "first.txt", "second.txt"];
        Program.Check(files.SortField == FileSortField.Name && files.IsSortAscending &&
            files.VisibleFiles.Select(static item => item.Name).SequenceEqual(byName),
            "file browser starts sorted by name with folders first");
        Program.Check(ReferenceEquals(window.FindControl<Button>("NameSortHeader")!.Command, files.SortByNameCommand) &&
            window.FindControl<Button>("SizeSortHeader")!.Command is not null &&
            window.FindControl<Button>("ModifiedSortHeader")!.Command is not null,
            "details column headers expose sort commands");

        Select(files, files.VisibleFiles.Single(static item => item.Name == "first.txt"));
        await files.SortBySizeCommand.Execute().ToTask();
        Program.Check(files.IsSizeSort && files.IsSortAscending &&
            files.VisibleFiles.Select(static item => item.Name).SequenceEqual(
                ["a very long folder name that wraps onto a second line", "folder", "second.txt", "first.txt"]) &&
            ReferenceEquals(files.SelectedItem, files.VisibleFiles.Single(static item => item.Name == "first.txt")),
            "size header sorts folders first, ascending by bytes, and preserves selection");
        await files.SortBySizeCommand.Execute().ToTask();
        Program.Check(files.IsSizeDescending &&
            files.VisibleFiles.Select(static item => item.Name).SequenceEqual(
                ["a very long folder name that wraps onto a second line", "folder", "first.txt", "second.txt"]),
            "clicking the active size header reverses the order");
        await files.SortByModifiedCommand.Execute().ToTask();
        Program.Check(files.IsModifiedSort && files.IsSortAscending &&
            files.VisibleFiles.Select(static item => item.Name).SequenceEqual(
                ["folder", "a very long folder name that wraps onto a second line", "first.txt", "second.txt"]),
            "modified header sorts by timestamp with folders first");
        await files.SortByNameCommand.Execute().ToTask();
        await files.SelectAllCommand.Execute().ToTask();
        Program.Check(files.SelectionCount == 4, "select all selects every visible row");
        files.Selection.Clear();
        list.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Key.A, KeyModifiers = KeyModifiers.Control
        });
        Program.Check(files.SelectionCount == 4, "Ctrl+A selects every visible row");
        files.Selection.Clear();
        await files.ShowIconsCommand.Execute().ToTask();
        window.UpdateLayout();
        await UntilAsync(() => list.GetRealizedContainers().Count() >= 4, ct);
        ListBoxItem[] sparseRows = list.GetRealizedContainers().OfType<ListBoxItem>().Take(3).ToArray();
        Program.Check(sparseRows.Length == 3 && sparseRows.All(row => row.Bounds.Width is >= 100 and < 200),
            "sparse icon rows keep tile width instead of dividing the whole viewport");
        ListBoxItem[] iconRows = list.GetRealizedContainers().OfType<ListBoxItem>().Take(4).ToArray();
        double[] iconTops = iconRows.Select(row => row.GetVisualDescendants().OfType<LucideIcon>().First()
            .TranslatePoint(default, list)?.Y ?? double.NaN).ToArray();
        double[] nameTops = iconRows.Select(row => row.GetVisualDescendants().OfType<TextBlock>().First()
            .TranslatePoint(default, list)?.Y ?? double.NaN).ToArray();
        Program.Check(iconRows.Length == 4 && iconTops.Max() - iconTops.Min() < 0.5 && nameTops.Max() - nameTops.Min() < 0.5,
            "one- and two-line file names keep icons and first name lines on the same row");
        if (string.Equals(((TopLevel)window).PlatformImpl?.GetType().Namespace, "Avalonia.Headless", StringComparison.Ordinal))
        {
            files.Selection.Clear();
            await UntilAsync(() => list.GetRealizedContainers().Count() >= 4, ct);
            double bottom = list.GetRealizedContainers()
                .Select(container => container.TranslatePoint(new Point(0, container.Bounds.Height), list)?.Y ?? 0).Max();
            var start = new Point(Math.Max(4, list.Bounds.Width - 12), Math.Min(list.Bounds.Height - 4, bottom + 24));
            var end = new Point(2, 2);
            Point startWindow = list.TranslatePoint(start, window) ?? start;
            Point endWindow = list.TranslatePoint(end, window) ?? end;
            window.MouseDown(startWindow, MouseButton.Left, RawInputModifiers.None);
            window.MouseMove(endWindow, RawInputModifiers.None);
            Program.Check(window.FindControl<Border>("SelectionBox")!.IsVisible, "mouse drag in empty list space displays a selection rectangle");
            window.MouseUp(endWindow, MouseButton.Left, RawInputModifiers.None);
            Program.Check(files.SelectionCount == 4 && !window.FindControl<Border>("SelectionBox")!.IsVisible,
                "mouse marquee selects intersecting files and hides after release");

            FileBrowserItem first = files.VisibleFiles[0];
            FileBrowserItem second = files.VisibleFiles[1];
            SelectMany(files, [first, second]);
            Control? secondRow = list.ContainerFromItem(second);
            Point pressPoint = secondRow is null
                ? new Point(20, 20)
                : secondRow.TranslatePoint(new Point(20, secondRow.Bounds.Height / 2), window) ?? new Point(20, 20);
            window.MouseDown(pressPoint, MouseButton.Left, RawInputModifiers.None);
            Program.Check(files.SelectionCount == 2, "pressing a multi-selected row preserves the selection for dragging");
            window.MouseUp(pressPoint, MouseButton.Left, RawInputModifiers.None);
            Program.Check(files.SelectionCount == 1 && ReferenceEquals(files.SelectedItem, second),
                "releasing without dragging collapses to the pressed row");
        }
        Program.Check(new FileBrowserItem(new FileEntry("photo.JPG", false, 0, 0, "application/octet-stream"), "/photo").IconKind == LucideIconKind.FileImage &&
            new FileBrowserItem(new FileEntry("archive.zip", false, 0, 0, null), "/archive").IconKind == LucideIconKind.FileArchive &&
            new FileBrowserItem(new FileEntry("app.apk", false, 0, 0, null), "/app").IconKind == LucideIconKind.Package &&
            new FileBrowserItem(new FileEntry("notes.json", false, 0, 0, null), "/notes").IconKind == LucideIconKind.FileBraces &&
            new FileBrowserItem(new FileEntry("clip", false, 0, 0, "video/mp4"), "/clip").IconKind == LucideIconKind.Film &&
            new FileBrowserItem(new FileEntry("folder-name", true, 0, 0, null), "/folder").IconKind == LucideIconKind.Folder,
            "file icons distinguish common extensions and MIME types");

        SelectMany(files, files.VisibleFiles.Where(static item => !item.IsDir));
        string manyTarget = Path.Combine(temporary, "many-download");
        Directory.CreateDirectory(manyTarget);
        await files.DownloadManyAsync(files.SelectedItems, manyTarget, ct);
        Program.Check(Directory.GetFiles(manyTarget).Length == 2 &&
            Directory.GetFiles(manyTarget).All(path => File.ReadAllBytes(path).SequenceEqual(MockPhone.Payload)),
            "multi-file download writes every selected file with atomic names");

        if (string.Equals(((TopLevel)window).PlatformImpl?.GetType().Namespace, "Avalonia.Headless", StringComparison.Ordinal) &&
            list.ContextFlyout is MenuFlyout menu)
        {
            menu.ShowAt(list);
            await Task.Delay(50, ct);
            MenuItem[] items = menu.Items.OfType<MenuItem>().ToArray();
            Program.Check(items.Single(item => Equals(item.Header, "下载")).IsEnabled &&
                items.Single(item => Equals(item.Header, "复制路径")).IsEnabled &&
                items.Single(item => Equals(item.Header, "删除")).IsEnabled,
                "context menu actions follow the current multi-selection");
            MenuItem sortMenu = items.Single(item => Equals(item.Header, "排序"));
            MenuItem[] sortItems = sortMenu.Items.OfType<MenuItem>().ToArray();
            Program.Check(items.Any(item => Equals(item.Header, "全选")) && items.Any(item => Equals(item.Header, "刷新")) &&
                sortItems.Single(item => Equals(item.Header, "名称")).IsChecked == true &&
                sortItems.Single(item => Equals(item.Header, "升序")).IsChecked == true,
                "context menu exposes select all, refresh and the active sort state");
            menu.Hide();
        }

        FileBrowserItem deleteItem = files.VisibleFiles.First(static item => item.Name == "first.txt");
        Select(files, deleteItem);
        await files.DeleteSelectionAsync(ct);
        Program.Check(phone.DeleteRequests.Any(requested => requested.Contains(deleteItem.Path)) &&
            files.VisibleFiles.All(item => item.Name != "first.txt") && files.SelectionCount == 0,
            "delete sends the selected path and refreshes the current directory");

        await FileBrowserCreateFolderChecks.RunAsync(files, window, phone, ct);
        phone.FileListings.Clear();
        await files.NavigateAsync(root);
    }

    private static void SelectMany(FileBrowserViewModel files, IEnumerable<FileBrowserItem> items)
    {
        using (files.Selection.BatchUpdate())
        {
            files.Selection.Clear();
            foreach (FileBrowserItem item in items)
            {
                int index = files.VisibleFiles.IndexOf(item);
                if (index >= 0) files.Selection.Select(index);
            }
        }
    }

    private static void Select(FileBrowserViewModel files, FileBrowserItem item)
    {
        files.Selection.Clear();
        int index = files.VisibleFiles.IndexOf(item);
        if (index >= 0) files.Selection.Select(index);
    }

    private static async Task CheckDirectCopyAsync(string temporary, CancellationToken ct)
    {
        string directory = Path.Combine(temporary, "direct-copy");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "same.txt"), "keep");
        byte[] first = Encoding.UTF8.GetBytes("first");
        byte[] second = Encoding.UTF8.GetBytes("second");
        await using (var batch = new FileDragBatch(_ => Task.FromResult<FileDragSource[]>([
            new("same.txt", first.Length, (stream, token) => stream.WriteAsync(first, token).AsTask()),
            new("same.txt", second.Length, (stream, token) => stream.WriteAsync(second, token).AsTask())
        ]), ct))
        {
            await batch.CopyToDirectoryAsync(directory);
        }
        Program.Check(File.ReadAllText(Path.Combine(directory, "same.txt")) == "keep" &&
            File.ReadAllBytes(Path.Combine(directory, "same (2).txt")).SequenceEqual(first) &&
            File.ReadAllBytes(Path.Combine(directory, "same (3).txt")).SequenceEqual(second) &&
            !Directory.EnumerateFiles(directory, ".andromeld-*.part").Any(),
            "a direct KDE drop keeps existing files and numbers same-name inputs without leaving partial files");

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var batch = new FileDragBatch(_ => Task.FromResult<FileDragSource[]>([new("cancel.txt", 32, async (stream, token) =>
        {
            await stream.WriteAsync(new byte[32], token);
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        })]), stop.Token))
        {
            Task copy = batch.CopyToDirectoryAsync(directory);
            await started.Task.WaitAsync(ct);
            await stop.CancelAsync();
            try { await copy; throw new Exception("Canceled direct copy completed"); }
            catch (OperationCanceledException) { Program.Check(true, "canceling a direct KDE drop interrupts its in-flight download"); }
        }
        Program.Check(!File.Exists(Path.Combine(directory, "cancel.txt")) &&
            !Directory.EnumerateFiles(directory, ".andromeld-*.part").Any(),
            "a canceled direct drop removes its partial file without publishing a target");
    }

    private static async Task UntilAsync(Func<bool> condition, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stop.CancelAfter(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(25, stop.Token);
    }
}
