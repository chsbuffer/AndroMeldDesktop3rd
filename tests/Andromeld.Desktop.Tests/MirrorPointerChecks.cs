using System.Diagnostics;
using System.Text.Json;

using Andromeld.Desktop.Controls;
using Andromeld.Desktop.Services;
using Andromeld.Desktop.ViewModels;
using Andromeld.Desktop.Views;
using Andromeld.Testing;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Andromeld.Desktop.Tests;

internal static class MirrorPointerChecks
{
    public static async Task BackSettingAsync(MirrorWindow window, MirrorViewModel mirror, MockPhone server, ToggleSwitch toggle, string settingsPath, CancellationToken ct)
    {
        toggle.IsChecked = true;
        var screen = window.FindControl<VideoView>("Screen")!;
        var center = screen.TranslatePoint(new Point(screen.Bounds.Width / 2, screen.Bounds.Height / 2), window)!.Value;
        var before = server.MediaInputs.Count;
        window.MouseDown(center, MouseButton.Right);
        window.MouseUp(center, MouseButton.Right);
        await UntilAsync(() => server.MediaInputs.Count >= before + 2, ct);
        var keys = server.MediaInputs.Skip(before).ToArray();
        var restored = JsonSerializer.Deserialize<Settings>(File.ReadAllText(settingsPath))!;
        Program.Check(keys.Length == 2 && keys.All(i => i.SessionId == mirror.SessionId &&
                i.Message.GetProperty("k").GetString() == "key" && i.Message.GetProperty("code").GetInt32() == 4) &&
            keys.Select(i => i.Message.GetProperty("a").GetString()).SequenceEqual(["down", "up"]) &&
            restored.MirrorRightClickBackEnabled && new MirrorShortcutsViewModel(restored).RightClickBackEnabled,
            $"{mirror.Subtitle}: settings toggle immediately enables right-click Back and persists across reload");
        if (mirror.IsPhone)
        {
            Program.Check(ToolTip.GetTip(window.FindControl<Button>("BackButton")!)?.ToString() == "返回（Esc / 鼠标右键）",
                "phone Back hint includes right click only while the setting is enabled");
        }

        toggle.IsChecked = false;
        restored = JsonSerializer.Deserialize<Settings>(File.ReadAllText(settingsPath))!;
        Program.Check(!restored.MirrorRightClickBackEnabled && !mirror.Shortcuts.RightClickBackEnabled &&
            (!mirror.IsPhone || ToolTip.GetTip(window.FindControl<Button>("BackButton")!)?.ToString() == "返回（Esc）"),
            "disabling right-click Back persists and immediately restores the open mirror's menu behavior and hint");
    }

    public static async Task RunAsync(MirrorWindow window, MirrorViewModel mirror, MockPhone server, CancellationToken ct)
    {
        window.UpdateLayout();
        var screen = window.FindControl<VideoView>("Screen")!;
        var center = screen.TranslatePoint(new Point(screen.Bounds.Width / 2, screen.Bounds.Height / 2), window)!.Value;
        var before = Inputs().Count;
        window.MouseMove(center);
        await UntilAsync(() => Inputs().Count > before, ct);
        var hover = Inputs().Skip(before).Single();
        Program.Check(hover.GetProperty("k").GetString() == "pointer" && hover.GetProperty("a").GetString() == "hover" &&
            hover.GetProperty("x").GetInt32() == mirror.Frame!.Width / 2 && hover.GetProperty("y").GetInt32() == mirror.Frame.Height / 2,
            $"{mirror.Subtitle}: unpressed mouse movement sends hover in video coordinates");

        before = Inputs().Count;
        var held = Stopwatch.StartNew();
        window.MouseDown(center, MouseButton.Right);
        window.MouseUp(center, MouseButton.Right);
        await UntilAsync(() => Inputs().Count > before, ct);
        await Task.Delay(150, ct);
        Program.Check(Inputs().Skip(before).Select(i => i.GetProperty("a").GetString()).SequenceEqual(["down"]),
            $"{mirror.Subtitle}: quick right click remains pressed for the Web context-menu gesture");
        // Hover must not disturb the pending long press after the physical release.
        window.MouseMove(center + new Vector(8, 8));
        await UntilAsync(() => Inputs().Count >= before + 2, ct);
        var context = Inputs().Skip(before).ToArray();
        Program.Check(held.ElapsedMilliseconds >= 590 && context.Length == 2 &&
            context.All(i => i.GetProperty("k").GetString() == "pointer") &&
            context.Select(i => i.GetProperty("a").GetString()).SequenceEqual(["down", "up"]) &&
            context.All(i => i.GetProperty("x").GetInt32() == hover.GetProperty("x").GetInt32() &&
                i.GetProperty("y").GetInt32() == hover.GetProperty("y").GetInt32()),
            $"{mirror.Subtitle}: right click sends a 600ms long press at the original point without Android Back");

        IPointer? pointer = null;
        screen.AddHandler(InputElement.PointerPressedEvent, RememberPointer, RoutingStrategies.Bubble, handledEventsToo: true);
        try
        {
            before = Inputs().Count;
            window.MouseDown(center, MouseButton.Right);
            if (pointer is null) throw new Exception("Right press did not reach the video surface.");
            pointer.Capture(null);
            window.MouseUp(center, MouseButton.Right);
            await UntilAsync(() => Inputs().Count >= before + 2, ct);
            await Task.Delay(650, ct);
            Program.Check(Inputs().Skip(before).Select(i => i.GetProperty("a").GetString()).SequenceEqual(["down", "cancel"]),
                $"{mirror.Subtitle}: lost capture cancels the right press without a delayed release");
        }
        finally { screen.RemoveHandler(InputElement.PointerPressedEvent, RememberPointer); }

        void RememberPointer(object? sender, PointerPressedEventArgs e) => pointer = e.Pointer;
        List<JsonElement> Inputs() => server.MediaInputs.Where(i => i.SessionId == mirror.SessionId &&
            i.Message.GetProperty("c").GetString() == "input").Select(i => i.Message).ToList();
    }

    public static async Task ClosePendingAsync(MirrorWindow window, MirrorViewModel mirror, MockPhone server, CancellationToken ct)
    {
        var sessionId = mirror.SessionId;
        var screen = window.FindControl<VideoView>("Screen")!;
        var center = screen.TranslatePoint(new Point(screen.Bounds.Width / 2, screen.Bounds.Height / 2), window)!.Value;
        var before = server.MediaInputs.Count;
        window.MouseDown(center, MouseButton.Right);
        window.MouseUp(center, MouseButton.Right);
        window.Close();
        await UntilAsync(() => mirror.IsDisposed && server.ClosedMirrors.Contains(sessionId!), ct);
        await Task.Delay(650, ct);
        var input = server.MediaInputs.Skip(before).Where(i => i.SessionId == sessionId).Select(i => i.Message).ToArray();
        Program.Check(input.Select(i => i.GetProperty("a").GetString()).SequenceEqual(["down", "cancel"]),
            "closing during a pending context click cancels its press before media disposal without a delayed up");
    }

    private static async Task UntilAsync(Func<bool> condition, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }
}
