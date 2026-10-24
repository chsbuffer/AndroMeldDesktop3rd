using System.Text.Json;
using Andromeld.Desktop.Controls;
using Andromeld.Desktop.ViewModels;
using Andromeld.Desktop.Views;
using Andromeld.Testing;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.LogicalTree;
using ReactiveUI.Primitives;

namespace Andromeld.Desktop.Tests;

internal static class MirrorInputMethodChecks
{
    public static async Task RunAsync(MainViewModel model, MainWindow main, MockPhone server, CancellationToken ct)
    {
        await model.OpenPhoneCommand.Execute().ToTask();
        model.SelectedApp = model.Apps.First(a => !a.App.Game);
        await model.OpenSelectedAppCommand.Execute().ToTask();
        await UntilAsync(() => model.Mirrors.Count == 2 && model.Mirrors.All(m => m.HasFrame), ct);

        foreach (var mirror in model.Mirrors.ToArray())
        {
            var window = main.MirrorWindows.Single(w => w.DataContext == mirror);
            window.UpdateLayout();
            var screen = window.FindControl<VideoView>("Screen")!;
            screen.Focus();
            var request = new TextInputMethodClientRequestedEventArgs { RoutedEvent = InputElement.TextInputMethodClientRequestedEvent };
            screen.RaiseEvent(request);
            Program.Check(request.Client is { SupportsPreedit: true }, $"{mirror.Subtitle}: focused video enables the system input method with composition support");
            var client = request.Client!;
            var anchor = new Point(screen.Bounds.Width * .65, screen.Bounds.Height * .55);
            var point = screen.TranslatePoint(anchor, window)!.Value;
            var pointerBefore = Inputs(mirror).Length;
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            await UntilAsync(() => Inputs(mirror).Length >= pointerBefore + 2, ct);
            Program.Check(client.CursorRectangle.TopLeft == anchor && client.CursorRectangle.Height > 0,
                $"{mirror.Subtitle}: input method candidates are anchored at the clicked video position");
            var before = Inputs(mirror).Length;
            var other = model.Mirrors.Single(m => m != mirror);
            var otherBefore = Inputs(other).Length;

            client.SetPreeditText("zhong", 5);
            window.UpdateLayout();
            var popup = screen.GetLogicalDescendants().OfType<Popup>().Single(p => p.IsOpen);
            Program.Check(popup.Child is Border { Child: TextBlock { Text: "zhong" } } && screen.IsFocused,
                $"{mirror.Subtitle}: composition text is visible without stealing keyboard focus");
            foreach (var (key, physical) in new[] { (Key.Enter, PhysicalKey.Enter), (Key.Back, PhysicalKey.Backspace),
                (Key.Left, PhysicalKey.ArrowLeft), (Key.Down, PhysicalKey.ArrowDown), (Key.Escape, PhysicalKey.Escape) })
            {
                window.KeyPress(key, RawInputModifiers.None, physical, null);
                window.KeyRelease(key, RawInputModifiers.None, physical, null);
            }
            Program.Check(Inputs(mirror).Length == before, $"{mirror.Subtitle}: composition and candidate keys do not reach Android");

            window.KeyTextInput("中文输入🙂");
            await UntilAsync(() => Inputs(mirror).Length > before, ct);
            Program.Check(Inputs(mirror).Skip(before).Single().GetProperty("text").GetString() == "中文输入🙂" &&
                Inputs(other).Length == otherBefore && !popup.IsOpen,
                $"{mirror.Subtitle}: committed Unicode text reaches only its mirror once and dismisses composition");

            before = Inputs(mirror).Length;
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            window.KeyTextInput("a");
            await UntilAsync(() => Inputs(mirror).Length >= before + 3, ct);
            var normal = Inputs(mirror).Skip(before).ToArray();
            Program.Check(normal.Length == 3 && normal.Take(2).All(i => i.GetProperty("code").GetInt32() == 66) &&
                normal[2].GetProperty("text").GetString() == "a", $"{mirror.Subtitle}: Enter and ordinary text resume after composition");

            client.SetPreeditText("cancel", 6);
            window.Focus();
            Program.Check(!popup.IsOpen, $"{mirror.Subtitle}: losing video focus clears local composition");
            screen.Focus();
            before = Inputs(mirror).Length;
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            await UntilAsync(() => Inputs(mirror).Length >= before + 2, ct);
            Program.Check(Inputs(mirror).Skip(before).All(i => i.GetProperty("code").GetInt32() == 4),
                $"{mirror.Subtitle}: cancelled composition does not swallow the next Android Back");
        }

        foreach (var mirror in model.Mirrors.ToArray())
        {
            var window = main.MirrorWindows.Single(w => w.DataContext == mirror);
            var screen = window.FindControl<VideoView>("Screen")!;
            screen.Focus();
            var request = new TextInputMethodClientRequestedEventArgs { RoutedEvent = InputElement.TextInputMethodClientRequestedEvent };
            screen.RaiseEvent(request);
            request.Client!.SetPreeditText("closing", 7);
            var popup = screen.GetLogicalDescendants().OfType<Popup>().Single(p => p.IsOpen);
            window.Close();
            await mirror.DisposeAsync();
            Program.Check(!popup.IsOpen, $"{mirror.Subtitle}: closing a mirror dismisses composition");
        }

        JsonElement[] Inputs(MirrorViewModel mirror) => server.MediaInputs.Where(i => i.SessionId == mirror.SessionId).Select(i => i.Message).ToArray();
    }

    private static async Task UntilAsync(Func<bool> condition, CancellationToken ct)
    {
        while (!condition()) await Task.Delay(20, ct);
    }
}
