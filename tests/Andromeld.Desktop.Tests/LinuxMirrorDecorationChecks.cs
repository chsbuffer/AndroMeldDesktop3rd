using Andromeld.Desktop.Controls;
using Andromeld.Desktop.ViewModels;
using Andromeld.Desktop.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using ReactiveUI.Primitives;

namespace Andromeld.Desktop.Tests;

internal static class LinuxMirrorDecorationChecks
{
    internal static async Task RunAsync(MirrorWindow window, MirrorViewModel mirror, CancellationToken ct)
    {
        var root = window.GetVisualAncestors().Last();
        var surface = window.FindControl<VideoView>("Screen")!;
        await UntilAsync(() => surface.Frame is not null && mirror.TitleBarHeight > 0, ct);
        var titlebar = root.GetVisualDescendants().OfType<Grid>().Single(grid => grid.Name == "PART_TitleBar");
        var back = Button("TitleBackButton");
        Program.Check(titlebar.IsEffectivelyVisible && back.IsEffectivelyEnabled && Button("TitleDisplayButton").IsEffectivelyVisible &&
            ReferenceEquals(back.Command, mirror.BackCommand), "Linux ordinary application displays custom Back and settings buttons");
        Program.Check(mirror.ImmersiveTitleBarEnabled && surface.OverlayTop > 0 && mirror.DisplayConfiguration.DecorationTop > 0,
            "Linux immersive titlebar overlaps video and reserves the Android decoration inset");

        var original = surface.Frame;
        try
        {
            SetColor(Colors.Black);
            await UntilAsync(() => window.CaptionTheme == ThemeVariant.Dark && back.ActualThemeVariant == ThemeVariant.Dark, ct);
            SetColor(Colors.White);
            await UntilAsync(() => window.CaptionTheme == ThemeVariant.Light && back.ActualThemeVariant == ThemeVariant.Light, ct);
            Program.Check(true, "Linux titlebar buttons follow dark and light video colors");

            await mirror.ToggleImmersiveTitleBarCommand.Execute().ToTask();
            await UntilAsync(() => !mirror.ImmersiveTitleBarEnabled && surface.OverlayTop == 0, ct);
            window.UpdateLayout();
            var videoTop = surface.TranslatePoint(default, root)!.Value.Y;
            var titleBottom = titlebar.TranslatePoint(new Point(0, titlebar.Bounds.Height), root)!.Value.Y;
            Program.Check(mirror.DisplayConfiguration.DecorationTop == 0 && videoTop >= titleBottom - .1,
                "Linux disabling immersion moves video below the custom titlebar");

            var color = Color.FromRgb(30, 100, 180);
            SetColor(color);
            await UntilAsync(() => window.CaptionBackground is SolidColorBrush brush && brush.Color == color, ct);
            Program.Check(true, "Linux non-immersive titlebar samples the video's top color");
            await mirror.ToggleImmersiveTitleBarCommand.Execute().ToTask();
            await UntilAsync(() => mirror.ImmersiveTitleBarEnabled && surface.OverlayTop > 0, ct);
            Program.Check(mirror.DisplayConfiguration.DecorationTop > 0, "Linux immersive titlebar can be enabled again");

            Button("PART_FullScreenButton").RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            await UntilAsync(() => window.WindowState == WindowState.FullScreen && !titlebar.IsEffectivelyVisible, ct);
            Program.Check(mirror.DisplayConfiguration.DecorationTop == 0, "Linux custom fullscreen button hides the titlebar and Android inset");
            Button("PART_PopoverFullScreenButton").RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            await UntilAsync(() => window.WindowState == WindowState.Normal && titlebar.IsEffectivelyVisible && surface.OverlayTop > 0, ct);
            Program.Check(window.IsExtendedIntoWindowDecorations, "Linux exiting fullscreen restores the custom immersive titlebar");
        }
        finally { surface.SetCurrentValue(VideoView.FrameProperty, original); }

        Button Button(string name) => root.GetVisualDescendants().OfType<Button>().Single(button => button.Name == name);
        void SetColor(Color color)
        {
            int width = Math.Max(1, (int)surface.Bounds.Width), height = Math.Max(1, (int)surface.Bounds.Height);
            var pixels = new byte[width * height * 4];
            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = color.B; pixels[i + 1] = color.G; pixels[i + 2] = color.R; pixels[i + 3] = 255;
            }
            surface.SetCurrentValue(VideoView.FrameProperty, new(width, height, pixels));
        }
    }

    private static async Task UntilAsync(Func<bool> condition, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(25, timeout.Token);
    }
}
