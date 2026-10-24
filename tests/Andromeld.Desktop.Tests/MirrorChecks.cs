using Andromeld.Desktop.Controls;
using Andromeld.Desktop.Services;
using Andromeld.Desktop.ViewModels;
using Andromeld.Desktop.Views;
using Andromeld.Protocol;
using Andromeld.Testing;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using ReactiveUI.Primitives;

namespace Andromeld.Desktop.Tests;

internal static class MirrorChecks
{
    public static async Task RunWithoutFfmpegAsync(MainViewModel model, MainWindow main, MockPhone server, string temporary, CancellationToken ct)
    {
        var configured = Environment.GetEnvironmentVariable("ANDROMELD_FFMPEG_LIBS");
        Environment.SetEnvironmentVariable("ANDROMELD_FFMPEG_LIBS", Path.Combine(temporary, "no-ffmpeg"));
        try
        {
            await model.OpenPhoneCommand.Execute().ToTask();
            var mirror = model.Mirrors.Single();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            while (!mirror.HasError) await Task.Delay(25, timeout.Token);
            Program.Check(mirror.Error.Contains("FFmpeg", StringComparison.Ordinal) && !mirror.CanControl && model.IsConnected,
                "missing FFmpeg stops only the mirror and reports an actionable error");
            await model.RefreshAppsCommand.Execute().ToTask();
            FileBrowserViewModel browser = model.FileBrowser;
            int fileIndex = browser.VisibleFiles.Select((item, index) => (item, index)).First(pair => !pair.item.IsDir).index;
            browser.Selection.Clear();
            browser.Selection.Select(fileIndex);
            var saved = Path.Combine(temporary, "without-ffmpeg.bin");
            await browser.DownloadAsync(browser.VisibleFiles[fileIndex], saved);
            Program.Check(model.Apps.Count == 18 && File.ReadAllBytes(saved).SequenceEqual(MockPhone.Payload),
                "application refresh and file download still work after decoder initialization fails");
            await TerminalChecks.RunWindowAsync(model, main, server, ct);
            await mirror.DisposeAsync();
            await model.DisconnectCommand.Execute().ToTask();
            await model.ConnectCommand.Execute().ToTask();
            Program.Check(model.IsConnected && model.Mirrors.Count == 0 && main.MirrorWindows.Count == 0 && model.CanOpenTerminal,
                "failed mirror cleanup and reconnection remain usable without FFmpeg");
        }
        finally { Environment.SetEnvironmentVariable("ANDROMELD_FFMPEG_LIBS", configured); }
    }

    public static async Task RunMockAsync(MainViewModel model, MainWindow main, MockPhone server, bool native, CancellationToken ct)
    {
        if (native)
        {
            await model.OpenPhoneCommand.Execute().ToTask();
            var phone = model.Mirrors.Single();
            await WaitAsync(() => phone.HasFrame, phone, main, native, ct);
            CheckBackend(main.MirrorWindows.Single());
            Program.Check(phone.Frame is { Width: 240, Height: 320 } && phone.CanControl,
                "platform phone mirror decodes the system FFmpeg frame in its own window");
            await phone.DisposeAsync();
        }

        model.SelectedApp = model.Apps.Single(a => a.App.Game);
        await model.OpenSelectedAppCommand.Execute().ToTask();
        var mirror = model.Mirrors.Single();
        await WaitAsync(() => mirror.HasFrame, mirror, main, native, ct);
        var window = main.MirrorWindows.Single();
        var viewport = window.FindControl<Grid>("Viewport")!;
        var expected = MirrorSize.FromViewport(viewport.Bounds.Width, viewport.Bounds.Height, window.RenderScaling);
        var request = server.OpenedMirrors.Last();
        Program.Check(window.WindowState == WindowState.Maximized && expected.Width > expected.Height &&
            request.GetProperty("w").GetInt32() == expected.Width && request.GetProperty("h").GetInt32() == expected.Height &&
            request.GetProperty("dpr").GetDouble() == expected.Dpr,
            $"game opens with measured landscape viewport and DPI instead of portrait defaults (state={window.WindowState}, request={request.GetProperty("w")}×{request.GetProperty("h")}@{request.GetProperty("dpr")}, viewport={expected.Width}×{expected.Height}@{expected.Dpr})");
        if (native) CheckBackend(window);
        await MirrorDisplayChecks.RunAsync(model, main, server, ct, native);
        server.RecoverTaskAfterBack = true;
        await mirror.BackCommand.Execute().ToTask();
        await Task.Delay(1000, ct);
        Program.Check(!mirror.IsDisposed && !mirror.IsTaskAway && mirror.ShowVideo, "transient task-away cancels the Web 850ms grace timer");
        server.RecoverTaskAfterBack = false;
        await mirror.BackCommand.Execute().ToTask();
        await WaitAsync(() => mirror.IsTaskAway, mirror, main, native, ct);
        Program.Check(!mirror.IsDisposed && !mirror.ShowVideo && mirror.CanRelaunch && main.MirrorWindows.Count == 1,
            "task-away shows relaunch and hides launcher video without closing the window, matching Web");
        await mirror.RelaunchCommand.Execute().ToTask();
        await WaitAsync(() => !mirror.IsTaskAway, mirror, main, native, ct);
        Program.Check(mirror.ShowVideo, "relaunch command restores video when the task becomes active");
        server.CloseWindowOnBack = true;
        await mirror.BackCommand.Execute().ToTask();
        await WaitAsync(() => mirror.IsDisposed && main.MirrorWindows.Count == 0, mirror, main, native, ct);
        await mirror.DisposeAsync();
        Program.Check(model.Mirrors.Count == 0 && main.MirrorWindows.Count == 0, "window-state.closed releases session and window without an error panel");
    }

    public static async Task RunLiveGameAsync(MainViewModel model, MainWindow main, string package, bool native, CancellationToken ct)
    {
        model.SelectedApp = model.Apps.Single(a => a.Package == package && a.App.UserId == 0);
        Program.Check(model.SelectedApp.App.Game, "real phone marks selected application as a game");
        await model.OpenSelectedAppCommand.Execute().ToTask();
        var mirror = model.Mirrors.Single();
        var window = main.MirrorWindows.Single();
        window.UpdateLayout();
        var viewport = window.FindControl<Grid>("Viewport")!;
        var target = MirrorSize.FromViewport(viewport.Bounds.Width, viewport.Bounds.Height, window.RenderScaling);
        await WaitAsync(() => mirror.Frame?.Width == target.Width && mirror.Frame.Height == target.Height, mirror, main, native, ct);
        // Keep receiving long enough to catch any delayed portrait resize after startup.
        await Task.Delay(1500, ct);
        Program.Check(window.WindowState == WindowState.Maximized && target.Width > target.Height &&
            mirror.Frame!.Width == target.Width && mirror.Frame.Height == target.Height && !mirror.HasError,
            $"live game remains landscape after startup: {mirror.Frame!.Width}×{mirror.Frame.Height}, dpr={target.Dpr:0.###}");
        if (native) CheckBackend(window);
        await mirror.DisposeAsync();
        Program.Check(model.Mirrors.Count == 0 && main.MirrorWindows.Count == 0 && model.IsConnected, "game close releases mirror while preserving control");
        Console.WriteLine("No phone screenshots or video were saved.");
    }

    public static async Task RunLiveAsync(MainViewModel model, MainWindow main, bool native, CancellationToken ct)
    {
        var app = model.Apps.Single(a => a.Package == "com.android.settings" && a.App.UserId == 0);
        await model.OpenPhoneCommand.Execute().ToTask();
        var phone = model.Mirrors.Single(m => m.IsPhone);
        await WaitAsync(() => phone.HasFrame, phone, main, native, ct);
        Console.WriteLine($"真机手机画面：{phone.Frame!.Width} × {phone.Frame.Height}");
        model.SelectedApp = app;
        await model.OpenSelectedAppCommand.Execute().ToTask();
        var application = model.Mirrors.Single(m => !m.IsPhone);
        await WaitAsync(() => application.HasFrame, application, main, native, ct);
        Program.Check(main.MirrorWindows.Count == 2 && model.Mirrors.Select(m => m.SessionId).Distinct().Count() == 2, "live phone and Settings mirror in independent windows");
        var appWindow = main.MirrorWindows.Single(w => w.DataContext == application);
        var phoneSize = (phone.Frame!.Width, phone.Frame.Height);
        foreach (var (width, height) in new[] { (760, 560), (375, 740), (640, 720) })
        {
            appWindow.Width = width; appWindow.Height = height;
            await WaitAsync(() => Math.Abs(appWindow.ClientSize.Width - width) < 2 && Math.Abs(appWindow.ClientSize.Height - height) < 2,
                application, main, native, ct);
            appWindow.UpdateLayout();
            var viewport = appWindow.FindControl<Grid>("Viewport")!;
            var target = MirrorSize.FromViewport(viewport.Bounds.Width, viewport.Bounds.Height, appWindow.RenderScaling);
            await WaitAsync(() => application.Frame!.Width == target.Width && application.Frame.Height == target.Height, application, main, native, ct);
            Program.Check(!application.HasError && application.CanControl, $"live resize {width}×{height} returns decoded {application.Frame!.Width}×{application.Frame.Height}");
            Console.WriteLine($"resize -> w={target.Width}, h={target.Height}, dpr={target.Dpr:0.###}");
        }
        Program.Check((phone.Frame!.Width, phone.Frame.Height) == phoneSize && !phone.HasError, "resizing application leaves physical phone screen unchanged");
        if (native)
        {
            foreach (var window in main.MirrorWindows) CheckBackend(window);
        }
        await application.BackCommand.Execute().ToTask();
        await WaitAsync(() => application.IsTaskAway || application.IsDisposed, application, main, native, ct);
        Program.Check(application.IsDisposed || application.IsTaskAway && !application.ShowVideo && application.CanRelaunch,
            "live Back follows server closed/task-away events instead of showing the virtual launcher");
        Console.WriteLine($"Settings Back: closed={application.IsDisposed}, away={application.IsTaskAway}");
        appWindow.Close();
        await application.DisposeAsync();
        Program.Check(model.Mirrors.Count == 1 && !phone.IsDisposed && model.IsConnected, "closing resized application keeps phone mirror and control connection alive");
        await phone.DisposeAsync();
        Program.Check(main.MirrorWindows.Count == 0 && model.Mirrors.Count == 0, "closing phone releases final mirror window");
        Console.WriteLine("Live mirror test kept screen content in memory; no phone screenshots or video were saved.");
    }

    private static void CheckBackend(MirrorWindow window)
    {
        var handle = window.TryGetPlatformHandle()?.HandleDescriptor;
        Console.WriteLine($"Window backend: {((TopLevel)window).PlatformImpl?.GetType().FullName}");
        var surface = window.FindControl<VideoView>("Screen")!;
        var expected = surface.UseNativeRendering && handle is "XID" or "HWND" ? "SDL3" : "Avalonia";
        Program.Check(surface.Backend == expected && surface.NativeError is null, $"{handle} mirror renders through {surface.Backend}");
        if (OperatingSystem.IsLinux() && window.DataContext is MirrorViewModel { IsPhone: false } mirror)
        {
            var wayland = DesktopPlatform.IsWayland;
            Program.Check(mirror.SupportsScreenCamera == wayland && mirror.CanUseScreenCamera == wayland && mirror.CanControl,
                "Linux enables screen camera on Wayland and keeps it unavailable on X11 while mirror control remains connected");
            var root = window.GetVisualAncestors().Last();
            Program.Check(window.IsExtendedIntoWindowDecorations && root.GetVisualDescendants().OfType<Button>()
                    .Any(button => button.Name == "TitleDisplayButton" && button.IsEffectivelyVisible),
                "Linux application mirror displays its custom titlebar and settings button");
        }
        else if (OperatingSystem.IsLinux())
        {
            Program.Check(!window.ExtendClientAreaToDecorationsHint && !window.IsExtendedIntoWindowDecorations,
                "Linux phone mirror keeps ordinary window decorations without the application titlebar extension");
        }
    }

    private static async Task WaitAsync(Func<bool> predicate, MirrorViewModel mirror, MainWindow main, bool native, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        while (!predicate())
        {
            if (mirror.HasError) throw new IOException(mirror.Error);
            main.UpdateLayout();
            foreach (var window in main.MirrorWindows) window.UpdateLayout();
            if (!native) AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            try { await Task.Delay(25, timeout.Token); }
            catch (OperationCanceledException) { throw new TimeoutException($"Mirror did not reach expected state. {mirror.Status}, {mirror.Resolution}"); }
        }
    }
}
