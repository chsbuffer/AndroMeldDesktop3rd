using Andromeld.Desktop.ViewModels;
using Andromeld.Desktop.Views;
using Andromeld.Protocol;
using Andromeld.Testing;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

using FluentAvalonia.UI.Controls;

namespace Andromeld.Desktop.Tests;

internal static class FileBrowserCreateFolderChecks
{
    internal static async Task RunAsync(FileBrowserViewModel files, FileBrowserWindow window, MockPhone phone, CancellationToken ct)
    {
        const string parent = "/storage/emulated/0/create-ui", elsewhere = "/storage/emulated/0/elsewhere";
        phone.FileListings[parent] = [];
        phone.FileListings[elsewhere] = [];
        await files.NavigateAsync(parent);
        var create = window.FindControl<Button>("CreateFolderButton")!;
        Program.Check(create.IsEnabled && !files.HasSelection, "new folder button is available in an empty directory without a selection");

        if (string.Equals(window.PlatformImpl?.GetType().Namespace, "Avalonia.Headless", StringComparison.Ordinal))
        {
            int before = phone.CreateDirectoryRequests.Count;
            Click(create);
            await UntilAsync(() => Dialog() is not null, ct);
            TextBox nameBox = Dialog()!.GetVisualDescendants().OfType<TextBox>().Single();
            Program.Check(nameBox.Text == "新建文件夹" && nameBox.SelectedText == "新建文件夹", "new folder dialog selects its default name for editing");
            Click(DialogButton("取消"));
            await UntilAsync(() => Dialog() is null, ct);
            Program.Check(phone.CreateDirectoryRequests.Count == before, "canceling the new folder dialog sends no request");

            var list = window.FindControl<ListBox>("FileList")!;
            var menu = (MenuFlyout)list.ContextFlyout!;
            menu.ShowAt(list);
            await Task.Delay(50, ct);
            var item = menu.Items.OfType<MenuItem>().Single(entry => Equals(entry.Header, "新建文件夹"));
            Program.Check(item.IsEnabled, "new folder context action is available on an empty file list");
            menu.Hide();
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await UntilAsync(() => Dialog() is not null, ct);
            nameBox = Dialog()!.GetVisualDescendants().OfType<TextBox>().Single();
            nameBox.Text = "../escape";
            await UntilAsync(() => !Dialog()!.IsPrimaryButtonEnabled, ct);
            Program.Check(!Dialog()!.IsPrimaryButtonEnabled && phone.CreateDirectoryRequests.Count == before,
                "invalid folder names disable creation without sending a request");
            nameBox.Text = "新建 #%.文件夹";
            await UntilAsync(() => Dialog()!.IsPrimaryButtonEnabled, ct);
            Click(DialogButton("创建"));
            await UntilAsync(() => phone.CreateDirectoryRequests.Count == before + 1 && !files.CreatingFolder && Dialog() is null, ct);
            Program.Check(phone.CreateDirectoryRequests.Last() == (parent, "新建 #%.文件夹") &&
                files.SelectedItem is { IsDir: true, Name: "新建 #%.文件夹" } && files.CurrentPath == parent,
                "the context action creates in the current directory, refreshes and selects the Unicode folder");

            Click(create);
            await UntilAsync(() => Dialog() is not null, ct);
            Dialog()!.GetVisualDescendants().OfType<TextBox>().Single().Text = "新建 #%.文件夹";
            Click(DialogButton("创建"));
            await UntilAsync(() => files.HasError && !files.CreatingFolder && Dialog() is null, ct);
            Program.Check(files.Error.Contains("eexist") && files.VisibleFiles.Count == 1 && create.IsEnabled,
                "duplicate-name errors remain visible and restore the create button without adding an entry");
        }

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        phone.FileMkdirReplyGate = gate;
        int requests = phone.CreateDirectoryRequests.Count;
        Task pending = files.CreateFolderAsync("late-folder", ct);
        try
        {
            await UntilAsync(() => phone.CreateDirectoryRequests.Count == requests + 1, ct);
            Program.Check(!create.IsEnabled, "creation disables repeated submissions while the device response is pending");
            Task navigation = files.NavigateAsync(elsewhere);
            gate.SetResult();
            try { await pending; throw new Exception("Navigation did not cancel folder creation"); }
            catch (OperationCanceledException) { }
            await navigation;
            Program.Check(files.CurrentPath == elsewhere && files.VisibleFiles.Count == 0 && !files.CreatingFolder && create.IsEnabled,
                "a late create response cannot refresh or select a folder after navigation");
        }
        finally
        {
            gate.TrySetResult();
            phone.FileMkdirReplyGate = null;
        }

        FAContentDialog? Dialog() => window.GetVisualDescendants().OfType<FAContentDialog>().FirstOrDefault();
        Button DialogButton(string text) => Dialog()!.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, text));
        void Click(Button button)
        {
            window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Point center = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
            window.MouseDown(center, MouseButton.Left, RawInputModifiers.None);
            window.MouseUp(center, MouseButton.Left, RawInputModifiers.None);
        }
    }

    private static async Task UntilAsync(Func<bool> condition, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
}
