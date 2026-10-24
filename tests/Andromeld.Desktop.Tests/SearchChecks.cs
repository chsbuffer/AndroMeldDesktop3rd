using Andromeld.Desktop.Services;
using Andromeld.Desktop.ViewModels;
using Andromeld.Desktop.Views;
using Andromeld.Testing;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using ReactiveUI.Primitives;

namespace Andromeld.Desktop.Tests;

internal static class SearchChecks
{
    public static async Task RunAsync(MainViewModel model, MainWindow main, MockPhone server, CancellationToken ct)
    {
        await model.ShowApplicationsCommand.Execute().ToTask();
        foreach (var query in new[] { "相册", "xiangce", "xc", "xiangc", "x ce", "TEST.APP0" })
        {
            model.Search = query;
            Program.Check(model.Apps.Count == 1 && model.Apps[0].Package == "test.app0" && !model.ShowSearchAction,
                "search matches literal, package, full/initial/mixed pinyin: " + query);
        }
        Program.Check(AppSearchMatcher.Matches("重庆银行", "chongqingyinhang") && AppSearchMatcher.Matches("重庆银行", "zqyh") &&
            !AppSearchMatcher.Matches("相册", "xzz"), "pinyin search handles polyphonic readings without false matches");

        model.Search = "test.app";
        Program.Check(model.Apps.Count > 0 && model.ShowSearchAction && model.SearchActionLabel == "在 Android 中打开",
            "a domain-shaped search offers an Android link action alongside matching apps, as in Web");
        model.Search = "custom://somewhere";
        Program.Check(model.ShowSearchAction && model.SearchActionLabel == "复制到 Android 剪贴板", "custom URI schemes fall back to copying text");
        model.Search = "https://";
        Program.Check(model.SearchActionLabel == "复制到 Android 剪贴板", "an invalid web address falls back to copying text");
        model.Search = "终端";
        Program.Check(model.ShowSearchTerminal && !model.ShowSearchAction, "matching a built-in tool suppresses the clipboard fallback");
        model.Search = "files";
        Program.Check(model.ShowSearchFiles && !model.ShowSearchAction, "built-in tools also match their Web English labels");
        await model.OpenSearchTerminalCommand.Execute().ToTask();
        var terminal = model.Terminals.Single();
        await model.OpenSearchTerminalCommand.Execute().ToTask();
        Program.Check(model.Terminals.Count == 1 && terminal.IsRunning && main.TerminalWindows.Count == 1,
            "the search terminal tool activates its existing running window instead of opening another session");
        main.TerminalWindows.Single().Close();
        await terminal.DisposeAsync();

        var search = main.FindControl<TextBox>("AppSearch")!;
        var action = main.FindControl<Button>("SearchAction")!;
        model.Search = "   synthetic search clipboard text   ";
        var wasClipboardEnabled = model.ClipboardSyncEnabled;
        model.ClipboardSyncEnabled = false;
        var clipboardCount = server.ClipboardMessages.Count(m => m.Type == 2);
        main.UpdateLayout();
        Program.Check(action.IsEffectivelyVisible && action.IsEnabled && !main.FindControl<StackPanel>("AppEmptyState")!.IsVisible &&
            server.ClipboardMessages.Count(m => m.Type == 2) == clipboardCount, "typing only presents the fallback; it does not write the phone clipboard");
        search.Focus();
        var presenter = search.GetVisualDescendants().OfType<TextPresenter>().Single();
        presenter.PreeditText = "zhong";
        Press(main, Key.Enter, PhysicalKey.Enter);
        Program.Check(server.ClipboardMessages.Count(m => m.Type == 2) == clipboardCount && model.SearchFeedback == "",
            "IME composition Enter does not execute a search action");
        presenter.PreeditText = null;
        Press(main, Key.Enter, PhysicalKey.Enter);
        await UntilAsync(() => model.SearchFeedback.Length > 0, ct);
        var copied = server.ClipboardMessages.Last(m => m.Type == 2).Message.GetProperty("text").GetString();
        Program.Check(copied == "synthetic search clipboard text" && !model.ClipboardSyncEnabled && model.Mirrors.Count == 0,
            "Enter explicitly sends trimmed text to Android even with automatic clipboard sharing disabled");
        Press(main, Key.Escape, PhysicalKey.Escape);
        Program.Check(model.Search == "" && model.Apps.Count == 18 && model.SearchFeedback == "", "Escape clears the pinned search and feedback");

        model.Search = "https://example.com/deep/path?query=original#fragment";
        main.UpdateLayout();
        var point = action.TranslatePoint(new Point(action.Bounds.Width / 2, action.Bounds.Height / 2), main)!.Value;
        main.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
        main.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
        await UntilAsync(() => model.Mirrors.Count == 1 && model.Mirrors[0].HasFrame, ct);
        var mirror = model.Mirrors.Single();
        var request = server.OpenedMirrors.Last();
        var window = main.MirrorWindows.Single();
        Program.Check(!mirror.IsPhone && mirror.App is null && mirror.CanControl && mirror.Intent?.Value == model.Search &&
            request.GetProperty("intentUrl").GetString() == model.Search && !request.TryGetProperty("pkg", out _),
            "one link-action click opens an independent intent mirror with application decorations and original deep-link data");
        await mirror.ZoomInCommand.Execute().ToTask();
        await mirror.CompatibilityAppCommand.Execute().ToTask();
        Program.Check(mirror.ZoomPercent == 110 && mirror.IsCompatibilityApp && mirror.CanScreenshot,
            "an unresolved intent window supports local display settings and screenshots without a fake app identity");
        await mirror.DisposeAsync();
        Program.Check(model.Mirrors.Count == 0 && main.MirrorWindows.Count == 0, "closing an intent mirror releases only its own window");

        model.Search = "test.app";
        main.UpdateLayout(); search.Focus();
        search.CaretIndex = 2;
        Press(main, Key.Right, PhysicalKey.ArrowRight);
        Program.Check(search.CaretIndex == 3, "left/right keeps editing the input before result navigation begins");
        Press(main, Key.Up, PhysicalKey.ArrowUp);
        var first = model.SelectedApp;
        Press(main, Key.Right, PhysicalKey.ArrowRight);
        Program.Check(first is not null && model.SelectedApp != first, "arrow navigation selects an app grid result while focus stays in search");
        Press(main, Key.Enter, PhysicalKey.Enter);
        await UntilAsync(() => model.Mirrors.Count == 1 && model.Mirrors[0].CanControl, ct);
        var selected = model.SelectedApp!;
        Program.Check(selected.IsRunning && model.Mirrors[0].App == selected.App, "Enter opens the keyboard-selected app and updates its running badge");
        await model.Mirrors[0].DisposeAsync();
        Program.Check(!selected.IsRunning, "the running badge clears when the mirror closes");
        model.Search = "";
        model.ClipboardSyncEnabled = wasClipboardEnabled;
    }

    private static async Task UntilAsync(Func<bool> condition, CancellationToken ct)
    {
        while (!condition()) await Task.Delay(20, ct);
    }

    private static void Press(Window window, Key key, PhysicalKey physical)
    {
        window.KeyPress(key, RawInputModifiers.None, physical, null);
        window.KeyRelease(key, RawInputModifiers.None, physical, null);
    }
}
