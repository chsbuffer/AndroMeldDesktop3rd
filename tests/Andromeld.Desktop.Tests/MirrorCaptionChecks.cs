using System.Runtime.Versioning;
using Andromeld.Desktop.Controls;
using Andromeld.Desktop.ViewModels;
using Andromeld.Desktop.Views;
using Andromeld.Platform.Windows;
using Andromeld.Testing;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Avalonia.Input;
using Avalonia.Styling;

namespace Andromeld.Desktop.Tests;

/// <summary>UI-thread checks of actual drawn decoration layers, which Headless does not create.</summary>
[SupportedOSPlatform("windows6.1")]
internal static class MirrorCaptionChecks
{
    internal static Button Button(MirrorWindow window, string name) =>
        Root(window).GetVisualDescendants().OfType<Button>().Single(button => button.Name == name);

    internal static void BeforeStart(MirrorWindow window, MirrorViewModel model)
    {
        var back = Button(window, "TitleBackButton");
        Program.Check(!back.IsEffectivelyEnabled && ReferenceEquals(back.Command, model.BackCommand),
            "frame Back binds the mirror command and stays disabled until its session is ready");
    }

    internal static async Task RunAsync(MirrorWindow window, MirrorViewModel model, MockPhone server, CancellationToken ct)
    {
        var back = Button(window, "TitleBackButton");
        Program.Check(back.IsEffectivelyEnabled && !back.Focusable,
            "frame Back enables when ready without taking keyboard focus");
        var viewport = window.FindControl<Grid>("Viewport")!;
        await UntilAsync(() => server.MediaInputs.Any(input => input.SessionId == model.SessionId &&
            input.Message.GetProperty("c").GetString() == "decor-insets" &&
            input.Message.GetProperty("top").GetInt32() == model.DisplayConfiguration.DecorationTop), ct);
        Program.Check(model.DisplayConfiguration.DecorationTop > 0,
            "application sends the overlapping titlebar as an Android decoration inset");
        var title = Root(window).GetVisualDescendants().OfType<TextBlock>().Single(text => text.Name == "MirrorCaptionTitle");
        await CheckCaptionThemeAsync();
        CheckHitTest(back, 1, "frame Back has native client hit testing");
        CheckHitTest(title, 2, "title text retains native window dragging hit testing");
        CheckHitTest(Button(window, "PART_FullScreenButton"), 1, "fullscreen uses client hit testing instead of the Windows snap maximize action");

        ClickControl(Button(window, "PART_FullScreenButton"), window);
        await UntilAsync(() => window.WindowState == WindowState.FullScreen, ct);
        await UntilAsync(() => model.DisplayConfiguration.DecorationTop == 0 && HasDecorationTop(0), ct);
        window.UpdateLayout();
        var titlebar = Root(window).GetVisualDescendants().OfType<Grid>().Single(control => control.Name == "PART_TitleBar");
        Program.Check(!titlebar.IsEffectivelyVisible && window.WindowDecorationMargin == default,
            "fullscreen removes the permanent titlebar, frame and Android decoration inset");
        var fullscreenViewport = model.ViewportSize;
        await RevealPopoverAsync();
        var session = model.SessionId;
        ClickControl(Button(window, "PopoverMinimizeButton"), window);
        await UntilAsync(() => window.WindowState == WindowState.Minimized, ct);
        Program.Check(model.CanControl && model.SessionId == session && !model.IsDisposed,
            "fullscreen minimize button minimizes the window while preserving its mirror session");
        window.WindowState = WindowState.FullScreen;
        window.Activate();
        await SettleAsync();
        await RevealPopoverAsync();
        var fullscreenDisplay = Button(window, "PopoverDisplayButton");
        Program.Check(ReferenceEquals(fullscreenDisplay.Flyout, window.DisplayMenu) &&
            ReferenceEquals(Button(window, "PopoverBackButton").Command, model.BackCommand),
            "fullscreen popover retains Back and display settings");
        ClickControl(fullscreenDisplay, window);
        await UntilAsync(() => window.DisplayMenu.IsOpen, ct);
        var fullscreenImmersive = (MenuItem)window.DisplayMenu.Items[2]!;
        await ClickWhenReadyAsync(fullscreenImmersive, ct);
        await UntilAsync(() => !model.ImmersiveTitleBarEnabled && !window.DisplayMenu.IsOpen, ct);
        Program.Check(model.ViewportSize == fullscreenViewport && model.DisplayConfiguration.DecorationTop == 0,
            "changing the saved titlebar preference in fullscreen leaves its video viewport unchanged");
        await RevealPopoverAsync();
        ClickControl(Button(window, "PART_PopoverFullScreenButton"), window);
        await UntilAsync(() => window.WindowState == WindowState.Normal, ct);
        await SettleAsync();
        Program.Check(!model.ImmersiveTitleBarEnabled && model.TitleBarHeight == 0,
            "exiting fullscreen restores the window and applies its saved non-immersive titlebar");
        CheckHitTest(viewport, 1, "non-immersive video is a client interaction area");
        var videoTop = viewport.PointToScreen(new(viewport.Bounds.Width / 2, 2));
        var titleBottom = titlebar.PointToScreen(new(0, titlebar.Bounds.Height));
        Program.Check(videoTop.Y >= titleBottom.Y &&
            WindowInput.SendMessage(window.TryGetPlatformHandle()!.Handle, 0x84, 0, Pack(videoTop.X, videoTop.Y)) == 1,
            "the first video rows lie below the titlebar and remain native client hits");

        window.WindowState = WindowState.Maximized;
        await SettleAsync();
        Click("PART_FullScreenButton");
        await UntilAsync(() => window.WindowState == WindowState.FullScreen, ct);
        Click("PART_PopoverFullScreenButton");
        await UntilAsync(() => window.WindowState == WindowState.Maximized, ct);
        Program.Check(true, "fullscreen entered from a maximized window restores maximization on exit");
        window.WindowState = WindowState.Normal;
        await SettleAsync();
        Click("PART_MinimizeButton");
        await UntilAsync(() => window.WindowState == WindowState.Minimized, ct);
        window.WindowState = WindowState.Normal;
        window.Activate();
        await SettleAsync();
        Program.Check(window.WindowState == WindowState.Normal && Button(window, "TitleBackButton").IsEffectivelyEnabled,
            "framework caption restore and minimize preserve the mirror session");

        void Click(string name) => Button(window, name).RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        async Task SettleAsync() { await Task.Delay(250, ct); window.UpdateLayout(); }
        bool HasDecorationTop(int top) => server.MediaInputs.LastOrDefault(input => input.SessionId == model.SessionId &&
            input.Message.GetProperty("c").GetString() == "decor-insets") is { Message.ValueKind: System.Text.Json.JsonValueKind.Object } input &&
            input.Message.GetProperty("top").GetInt32() == top;
        async Task RevealPopoverAsync()
        {
            var handle = window.TryGetPlatformHandle()!.Handle;
            // Use native pointer messages, without moving the user's system cursor.
            WindowInput.SendMessage(handle, 0x200, 0, Pack((int)(window.ClientSize.Width * window.RenderScaling / 2), 100));
            WindowInput.SendMessage(handle, 0x200, 0, Pack((int)(window.ClientSize.Width * window.RenderScaling / 2), 0));
            await UntilAsync(() => Button(window, "PART_PopoverFullScreenButton").IsEffectivelyVisible, ct);
            window.UpdateLayout();
            // Native input hit testing uses the last committed compositor scene.
            // Wait for the revealed controls to participate before clicking them.
            var button = Button(window, "PART_PopoverFullScreenButton");
            await UntilAsync(() => button.TranslatePoint(new(button.Bounds.Width / 2, button.Bounds.Height / 2), Root(window)) is { } point &&
                Root(window).GetVisualsAt(point).Any(visual => visual == button || button.IsVisualAncestorOf(visual)), ct);
        }
        void CheckHitTest(Control control, nint expected, string message)
        {
            var point = control.PointToScreen(new(control.Bounds.Width / 2, control.Bounds.Height / 2));
            var hit = WindowInput.SendMessage(window.TryGetPlatformHandle()!.Handle, 0x84, 0, Pack(point.X, point.Y));
            Program.Check(hit == expected && WindowInput.WindowFromPoint(new(point.X, point.Y)) == window.TryGetPlatformHandle()!.Handle,
                $"{message} without an SDL child covering it (hit={hit})");
        }
        async Task CheckCaptionThemeAsync()
        {
            var surface = window.FindControl<VideoView>("Screen")!;
            var original = surface.Frame;
            var width = Math.Max(1, (int)viewport.Bounds.Width);
            var height = Math.Max(1, (int)viewport.Bounds.Height);
            try
            {
                await CheckPaletteAsync(0, 255, ThemeVariant.Dark,
                    "a dark titlebar selects the dark theme independently of the bright body");
                await CheckPaletteAsync(255, 0, ThemeVariant.Light,
                    "a bright titlebar selects the light theme independently of the dark body");
                await CheckPaletteAsync(116, 0, ThemeVariant.Light,
                    "near-threshold titlebar brightness retains the light palette without flicker");
                await CheckPaletteAsync(0, 255, ThemeVariant.Dark,
                    "a clear transition to a dark titlebar changes the palette immediately");
                await CheckPaletteAsync(116, 255, ThemeVariant.Dark,
                    "near-threshold titlebar brightness also retains the dark palette without flicker");
                surface.SetCurrentValue(VideoView.FrameProperty, new(32, 32, new byte[32 * 32 * 4]));
                await UntilAsync(() => PaletteMatches(ThemeVariant.Light), ct);
                Program.Check(true, "top letterboxing uses its displayed light background instead of the dark video below it");

                async Task CheckPaletteAsync(byte header, byte body, ThemeVariant theme, string message)
                {
                    var colors = new byte[width * height * 4];
                    for (var i = 0; i < colors.Length; i += 4)
                    {
                        var value = i / 4 / width < Math.Ceiling(surface.OverlayTop) ? header : body;
                        colors[i] = colors[i + 1] = colors[i + 2] = value; colors[i + 3] = 255;
                    }
                    surface.SetCurrentValue(VideoView.FrameProperty, new(width, height, colors));
                    await UntilAsync(() => PaletteMatches(theme), ct);
                    window.UpdateLayout();
                    Program.Check(true, message);
                }
                bool PaletteMatches(ThemeVariant theme) => window.CaptionTheme == theme;
            }
            finally { surface.SetCurrentValue(VideoView.FrameProperty, original); }
        }
    }

    internal static void ClickBack(MirrorWindow window)
    {
        var button = Button(window, "TitleBackButton");
        ClickControl(button, window);
    }

    internal static async Task DisplayAsync(MirrorWindow window, MirrorViewModel model, MockPhone server, CancellationToken ct)
    {
        var display = Button(window, "TitleDisplayButton");
        Program.Check(!display.Focusable && ReferenceEquals(display.Flyout, window.DisplayMenu) &&
            WindowDecorationProperties.GetElementRole(display) == WindowDecorationsElementRole.User,
            "display menu is bound to the titlebar user button to the right of Back");
        ClickControl(display, window);
        await UntilAsync(() => window.DisplayMenu.IsOpen, ct);
        var immersive = (MenuItem)window.DisplayMenu.Items[2]!;
        Program.Check(!immersive.IsChecked, "display menu shows the saved non-immersive choice after fullscreen");
        await ClickWhenReadyAsync(immersive, ct);
        await UntilAsync(() => model.ImmersiveTitleBarEnabled && !window.DisplayMenu.IsOpen && model.TitleBarHeight > 0, ct);
        await UntilAsync(() => server.MediaInputs.Any(input => input.SessionId == model.SessionId &&
            input.Message.GetProperty("c").GetString() == "decor-insets" &&
            input.Message.GetProperty("top").GetInt32() == model.DisplayConfiguration.DecorationTop), ct);
        ClickControl(display, window);
        await UntilAsync(() => window.DisplayMenu.IsOpen, ct);
        Program.Check(immersive.IsChecked, "clicking the immersive toggle updates the video inset and the reopened menu checkmark");
        var zoomRow = (MenuItem)window.DisplayMenu.Items[0]!;
        var grid = (Grid)zoomRow.Header!;
        var zoomOut = grid.Children.OfType<Button>().Single(button => button.Name == "ZoomOutButton");
        var zoomIn = grid.Children.OfType<Button>().Single(button => button.Name == "ZoomInButton");
        var compatibility = (MenuItem)window.DisplayMenu.Items[1]!;
        var choices = compatibility.Items.OfType<MenuItem>().ToArray();
        await Task.Delay(100, ct);
        Program.Check(ReferenceEquals(zoomIn.Command, model.ZoomInCommand) && ReferenceEquals(zoomOut.Command, model.ZoomOutCommand) &&
            grid.Children.OfType<TextBlock>().Single().Text == "100%" && choices[0].IsChecked && !choices[1].IsChecked && !choices[2].IsChecked,
            "display popup binds zoom and the three exclusive compatibility choices");
        ClickControl(zoomIn, TopLevel.GetTopLevel(zoomIn)!);
        await UntilAsync(() => model.ZoomPercent == 110, ct);
        Program.Check(window.DisplayMenu.IsOpen && grid.Children.OfType<TextBlock>().Single().Text == "110%",
            "clicking plus updates the percentage and keeps the display menu open");
        ClickControl(zoomOut, TopLevel.GetTopLevel(zoomOut)!);
        await UntilAsync(() => model.ZoomPercent == 100, ct);
        compatibility.IsSubMenuOpen = true;
        await Task.Delay(150, ct);
        ClickControl(choices[1], TopLevel.GetTopLevel(choices[1])!);
        await UntilAsync(() => model.IsCompatibilityApp && !window.DisplayMenu.IsOpen, ct);
        Program.Check(model.IsCompatibilityApp, "clicking the current-app submenu choice applies compatibility and closes the menu");
        ClickControl(display, window);
        await UntilAsync(() => window.DisplayMenu.IsOpen, ct);
        compatibility.IsSubMenuOpen = true;
        await Task.Delay(100, ct);
        Program.Check(!choices[0].IsChecked && choices[1].IsChecked && !choices[2].IsChecked,
            "reopening the menu restores the effective compatibility checkmark");
        ClickControl(choices[0], TopLevel.GetTopLevel(choices[0])!);
        await UntilAsync(() => model.IsCompatibilityOff && !window.DisplayMenu.IsOpen, ct);
        ClickControl(display, window);
        await UntilAsync(() => window.DisplayMenu.IsOpen, ct);
        var inputCount = server.MediaInputs.Count(i => i.SessionId == model.SessionId &&
            i.Message.TryGetProperty("k", out var kind) && kind.GetString() == "key");
        var popup = TopLevel.GetTopLevel(zoomRow)!;
        WindowsInputChecks.InjectKey(popup.TryGetPlatformHandle()!.Handle, 0x1B, KeyModifiers.None, null);
        await UntilAsync(() => !window.DisplayMenu.IsOpen, ct);
        await Task.Delay(100, ct);
        Program.Check(server.MediaInputs.Count(i => i.SessionId == model.SessionId &&
            i.Message.TryGetProperty("k", out var kind) && kind.GetString() == "key") == inputCount,
            "Escape dismisses the display menu without sending an Android key");
        window.Activate();
    }

    internal static void ClickControl(Control button, TopLevel topLevel)
    {
        var point = button.PointToScreen(new(button.Bounds.Width / 2, button.Bounds.Height / 2));
        var native = new NativePoint(point.X, point.Y);
        var handle = topLevel.TryGetPlatformHandle()!.Handle;
        if (!WindowInput.ScreenToClient(handle, ref native)) throw new IOException("Cannot map frame button coordinates.");
        WindowInput.SendMessage(handle, 0x201, 1, Pack(native.X, native.Y));
        WindowInput.SendMessage(handle, 0x202, 0, Pack(native.X, native.Y));
    }

    private static async Task ClickWhenReadyAsync(Control control, CancellationToken ct)
    {
        var topLevel = TopLevel.GetTopLevel(control)!;
        var root = topLevel.GetVisualAncestors().LastOrDefault() ?? topLevel;
        await UntilAsync(() => control.Bounds.Width > 0 && control.IsEffectivelyVisible &&
            control.TranslatePoint(new(control.Bounds.Width / 2, control.Bounds.Height / 2), root) is { } point &&
            root.GetVisualsAt(point).Any(visual => visual == control || control.IsVisualAncestorOf(visual)), ct);
        ClickControl(control, topLevel);
    }

    private static Visual Root(MirrorWindow window) => window.GetVisualAncestors().Last();
    private static nint Pack(int x, int y) => unchecked((nint)(int)((uint)(ushort)x | ((uint)(ushort)y << 16)));
    private static async Task UntilAsync(Func<bool> condition, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(25, timeout.Token);
    }
}
