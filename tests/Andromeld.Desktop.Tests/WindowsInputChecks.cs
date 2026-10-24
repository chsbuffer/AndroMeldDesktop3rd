using System.Runtime.Versioning;
using Andromeld.Platform.Windows;
using static Andromeld.Platform.Windows.WindowInput;
using Andromeld.Desktop.Controls;
using Andromeld.Desktop.Services;
using Andromeld.Desktop.ViewModels;
using Andromeld.Desktop.Views;
using Andromeld.Protocol;
using Andromeld.Testing;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using ReactiveUI.Avalonia;

namespace Andromeld.Desktop.Tests;

/// <summary>Exercises Win32 mouse and keyboard routing through the real SDL host without moving the system cursor.</summary>
[SupportedOSPlatform("windows6.1")]
internal static class WindowsInputChecks
{
    private const uint WmNcHitTest = 0x84, WmMouseMove = 0x200, WmLeftDown = 0x201, WmLeftUp = 0x202;
    private const uint WmRightDown = 0x204, WmRightUp = 0x205, WmWheel = 0x20A;

    public static void Run(bool live, string? address)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Win32 input checks require Windows.");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(75));
        Exception? failure = null;
        var completed = false;
        AppBuilder.Configure<global::Andromeld.Desktop.App>().UseAndromeldPlatform()
            .UseSkia().UseHarfBuzz().WithInterFont().UseReactiveUI(_ => { }).SetupWithoutStarting();
        Dispatcher.UIThread.Post(async () =>
        {
            var temporary = Path.Combine(Path.GetTempPath(), "andromeld-input-" + Guid.NewGuid().ToString("N"));
            MainWindow? main = null;
            FileBrowserWindow? files = null;
            try
            {
                await using var server = live ? null : await MockPhone.StartAsync();
                var settings = live ? Settings.Load() : new Settings { StoragePath = Path.Combine(temporary, "settings.json") };
                var configuredAddress = address ?? settings.Address;
                var endpoint = server?.Endpoint ?? new UriBuilder(configuredAddress.Contains("://", StringComparison.Ordinal) ? configuredAddress : $"ws://{configuredAddress}") { Path = "/ws" }.Uri;
                var token = server is null ? settings.Tokens.GetValueOrDefault(endpoint.AbsoluteUri) : "test-token";
                if (token is null) throw new IOException("Live input check requires an existing pairing for this endpoint.");
                await using var client = new AndromeldClient();
                await client.ConnectAsync(endpoint, settings.ClientId, token, null, stop.Token);
                var apps = await client.GetAppsAsync(stop.Token);
                var app = live ? apps.Single(a => a.PackageName == "com.android.settings" && a.UserId == 0) : apps.First(a => !a.Game);
                var identities = new HashSet<string>(StringComparer.Ordinal);
                if (!live)
                {
                    main = new MainWindow(); files = new FileBrowserWindow();
                    main.Show(); files.Show();
                    CheckWindowIdentity(main, "Main", identities);
                    CheckWindowIdentity(files, "Files", identities);
                    var handle = main.TryGetPlatformHandle()!.Handle;
                    var identity = WindowTaskbarIdentity.Get(handle)!;
                    EventHandler<WindowClosingEventArgs> cancel = (_, e) => e.Cancel = true;
                    main.Closing += cancel; main.Close(); main.Closing -= cancel;
                    Program.Check(main.IsVisible && WindowTaskbarIdentity.Get(handle) == identity,
                        "canceling window close preserves its taskbar identity");
                    Program.Check(await Task.Run(() =>
                    {
                        try { WindowTaskbarIdentity.Get(handle); return false; }
                        catch (InvalidOperationException) { return true; }
                    }), "native property-store access rejects a non-owner thread");
                    WindowTaskbarIdentity.Set(handle, null);
                    Program.Check(WindowTaskbarIdentity.Get(handle) is null, "clearing AppUserModelID removes the native window property");
                    WindowTaskbarIdentity.Set(handle, identity);
                    await CheckMirrorAsync(client, null, server, settings, identities, stop.Token);
                }
                await CheckMirrorAsync(client, app, server, settings, identities, stop.Token);
                Console.WriteLine(live
                    ? "Live Settings input check used in-memory frames only; no phone images, credentials or clipboard contents were saved."
                    : "Win32/SDL input checks used synthetic phone data only; clipboard was not accessed.");
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                files?.Close(); main?.Close();
                if (Directory.Exists(temporary)) Directory.Delete(temporary, true);
                completed = true; stop.Cancel();
            }
        });
        Dispatcher.UIThread.MainLoop(stop.Token);
        if (!completed) throw new TimeoutException("Win32 input checks exceeded the watchdog.");
        if (failure is not null) throw new Exception("Win32 input checks failed", failure);
    }

    private static void CheckWindowIdentity(Window window, string kind, HashSet<string> identities)
    {
        var identity = WindowTaskbarIdentity.Get(window.TryGetPlatformHandle()!.Handle);
        Program.Check(identity is not null && identity.StartsWith($"AndroMeld.Desktop.{kind}.", StringComparison.Ordinal) && identities.Add(identity),
            $"{kind} has a unique native window AppUserModelID");
    }

    private static async Task CheckMirrorAsync(AndromeldClient client, PhoneApp? app, MockPhone? server, Settings settings, HashSet<string> identities, CancellationToken ct)
    {
        if (server is not null) server.RecoverTaskAfterBack = true;
        var iconPng = app is not null && server is not null ? Convert.FromBase64String(MockPhone.Png) : null;
        await using var mirror = new MirrorViewModel(client, app, ct, new(settings), iconPng, new(settings));
        var window = new MirrorWindow(mirror) { WindowStartupLocation = WindowStartupLocation.CenterScreen, Topmost = true };
        try
        {
            window.Show();
            CheckWindowIdentity(window, mirror.IsPhone ? "Phone" : "App", identities);
            if (iconPng is not null)
            {
                using var source = new MemoryStream(iconPng);
                var expected = new WindowIcon(source);
                using var expectedBytes = new MemoryStream();
                using var actualBytes = new MemoryStream();
                expected.Save(expectedBytes); window.Icon!.Save(actualBytes);
                var handle = window.TryGetPlatformHandle()!.Handle;
                Program.Check(actualBytes.ToArray().SequenceEqual(expectedBytes.ToArray()) &&
                    (SendMessage(handle, 0x7F, 0, 0) != 0 || SendMessage(handle, 0x7F, 1, 0) != 0),
                    "application window uses the supplied application icon and installs a native HICON");
            }
            await window.WaitForViewportAsync();
            if (!mirror.IsPhone && server is not null) MirrorCaptionChecks.BeforeStart(window, mirror);
            await mirror.StartAsync();
            await UntilAsync(() => mirror.HasFrame || mirror.HasError, ct);
            if (mirror.HasError) throw new IOException(mirror.Error);
            window.Activate();
            window.UpdateLayout();
            await Task.Delay(250, ct); // NativeControlHost positions its HWNDs after layout.
            if (!mirror.IsPhone && server is not null) await MirrorCaptionChecks.RunAsync(window, mirror, server, ct);
            if (!mirror.IsPhone && server is not null) await MirrorCaptionChecks.DisplayAsync(window, mirror, server, ct);
            var surface = window.FindControl<VideoView>("Screen")!;
            var expectedBackend = mirror.IsPhone ? "SDL3" : "Avalonia";
            Program.Check(surface.Backend == expectedBackend && surface.NativeError is null, $"{mirror.Subtitle}: real Win32 window uses {expectedBackend} video presentation");
            var root = window.TryGetPlatformHandle()!.Handle;
            var center = VideoPoint(surface, .5, .5);
            var screenCenter = surface.PointToScreen(center);
            if (mirror.IsPhone)
            {
                var holder = GetWindow(root, 5); // GW_CHILD: Avalonia's clipping/attachment HWND.
                var child = GetWindow(holder, 5); // The HWND wrapped by SDL.
                var childHit = SendMessage(child, WmNcHitTest, 0, Pack(screenCenter.X, screenCenter.Y));
                var holderHit = SendMessage(holder, WmNcHitTest, 0, Pack(screenCenter.X, screenCenter.Y));
                Program.Check(child != 0 && holder != 0 && childHit == -1 && holderHit == -1,
                    $"{mirror.Subtitle}: SDL child and Avalonia holder both pass hit testing to the top level (child={childHit}, holder={holderHit})");
            }
            else Program.Check(GetWindow(root, 5) == 0 && WindowFromPoint(new(screenCenter.X, screenCenter.Y)) == root,
                "application video and titlebar share the top-level compositor without a native child surface");

            if (server is not null)
            {
                var beforeHover = AllSessionInputs().Count;
                Inject(window, surface, center, WmMouseMove, 0);
                await UntilAsync(() => AllSessionInputs().Skip(beforeHover).Any(i => i.TryGetProperty("a", out var action) && action.GetString() == "hover"), ct);
                var hover = AllSessionInputs().Skip(beforeHover).Last(i => i.TryGetProperty("a", out var action) && action.GetString() == "hover");
                var mapped = surface.TryMap(surface.PointToClient(screenCenter), false, out var hoverPixel);
                Program.Check(mapped && hover.GetProperty("x").GetInt32() == hoverPixel.X && hover.GetProperty("y").GetInt32() == hoverPixel.Y,
                    $"{mirror.Subtitle}: native mouse movement without buttons reaches Android hover at the mapped video point");
            }

            var previousFrame = mirror.Frame!;
            var destination = VideoPoint(surface, .5, .2);
            Inject(window, surface, center, WmLeftDown, 1);
            await Task.Delay(40, ct);
            Inject(window, surface, destination, WmMouseMove, 1);
            await Task.Delay(40, ct);
            Inject(window, surface, destination, WmLeftUp, 0);
            if (server is not null)
            {
                // Windows may synthesize mouse-move messages when pointer capture changes.
                // Wait for release, then require one ordered drag while allowing those moves.
                await UntilAsync(() => PointerInputs() is { Count: >= 3 } input && input[^1].GetProperty("a").GetString() == "up", ct);
                var inputs = PointerInputs();
                Program.Check(inputs[0].GetProperty("a").GetString() == "down" && inputs[^1].GetProperty("a").GetString() == "up" &&
                    inputs.Skip(1).SkipLast(1).All(i => i.GetProperty("a").GetString() == "move"),
                    $"{mirror.Subtitle}: Win32 mouse drag reaches only its media session in order");
                // Win32 mouse coordinates are integer physical pixels, so account for
                // the same screen-to-DIP rounding that the native input path performs.
                var expected = surface.TryMap(surface.PointToClient(screenCenter), false, out var pixel);
                Program.Check(expected && inputs[0].GetProperty("x").GetInt32() == pixel.X && inputs[0].GetProperty("y").GetInt32() == pixel.Y,
                    $"{mirror.Subtitle}: video letterboxing at DPI scale {window.RenderScaling} maps to video pixel {pixel.X},{pixel.Y} (received {inputs[0].GetProperty("x")},{inputs[0].GetProperty("y")})");
                var beforeClick = inputs.Count;
                Inject(window, surface, center, WmLeftDown, 1);
                Inject(window, surface, center, WmLeftUp, 0);
                await UntilAsync(() => PointerInputs() is var input && input.Count >= beforeClick + 2 && input[^1].GetProperty("a").GetString() == "up", ct);
                Program.Check(PointerInputs().Skip(beforeClick).Select(i => i.GetProperty("a").GetString()).Where(static action => action != "move").SequenceEqual(["down", "up"]),
                    $"{mirror.Subtitle}: a mouse click sends one pointer down/up pair");
            }
            else
            {
                await UntilAsync(() => mirror.Frame is { } frame && !frame.Bgra.AsSpan().SequenceEqual(previousFrame.Bgra), ct);
                Program.Check(!mirror.HasError, "real Settings screen changes after a Win32 mouse drag through the video viewport");
            }

            if (server is not null)
            {
                surface.Focus();
                if (mirror.IsPhone)
                {
                    foreach (var option in mirror.Shortcuts.Options)
                    {
                        mirror.Shortcuts.SelectedModifier = option;
                        var count = SessionInputs().Count;
                        InjectKey(root, 0x52, option.Modifiers, 'R');
                        await UntilAsync(() => SessionInputs().Count >= count + 2, ct);
                        InjectKey(root, 0x48, option.Modifiers, 'H');
                        await UntilAsync(() => SessionInputs().Count >= count + 4, ct);
                        await Task.Delay(30, ct);
                        var keys = SessionInputs().Skip(count).ToArray();
                        Program.Check(keys.Length == 4 && keys.Select(i => i.GetProperty("code").GetInt32()).SequenceEqual([187, 187, 3, 3]) &&
                            keys.All(i => i.GetProperty("meta").GetInt32() == 0),
                            $"Win32 {option.Label}+R/H send navigation pairs without leaking WM_CHAR text");
                    }
                    var textCount = SessionInputs().Count;
                    InjectKey(root, 0x41, KeyModifiers.None, 'a');
                    await UntilAsync(() => SessionInputs().Count > textCount, ct);
                    Program.Check(SessionInputs().Skip(textCount).Single().GetProperty("text").GetString() == "a",
                        "ordinary text input still works after a handled Win32 shortcut");
                }
                else
                {
                    var count = SessionInputs().Count;
                    InjectKey(root, 0x1B, KeyModifiers.None, null);
                    await UntilAsync(() => SessionInputs().Count >= count + 2, ct);
                    Program.Check(SessionInputs().Skip(count).All(i => i.GetProperty("code").GetInt32() == 4),
                        "application Escape reaches Android Back through the real Win32 keyboard route");
                }
            }

            Inject(window, surface, center, WmWheel, 120u << 16);
            var beforeContext = server is null ? 0 : PointerInputs().Count;
            var beforeBack = server is null ? 0 : SessionInputs().Count(i => i.TryGetProperty("code", out var code) && code.GetInt32() == 4);
            Inject(window, surface, center, WmRightDown, 2);
            Inject(window, surface, center, WmRightUp, 0);
            if (server is not null)
            {
                await UntilAsync(() => PointerInputs().Skip(beforeContext).Any(i => i.GetProperty("a").GetString() == "up"), ct);
                var context = PointerInputs().Skip(beforeContext).ToArray();
                Program.Check(context.Select(i => i.GetProperty("a").GetString()).Where(static action => action != "move").SequenceEqual(["down", "up"]) &&
                    SessionInputs().Count(i => i.TryGetProperty("code", out var code) && code.GetInt32() == 4) == beforeBack,
                    $"{mirror.Subtitle}: native right click sends the Web long-press gesture without Back by default");
                mirror.Shortcuts.RightClickBackEnabled = true;
                var beforeNavigation = PointerInputs().Count;
                Inject(window, surface, center, WmRightDown, 2);
                Inject(window, surface, center, WmRightUp, 0);
                await UntilAsync(() => SessionInputs().Count(i => i.TryGetProperty("code", out var code) && code.GetInt32() == 4) >= (mirror.IsPhone ? 2 : 4), ct);
                Program.Check(SessionInputs().Any(i => i.TryGetProperty("k", out var kind) && kind.GetString() == "scroll" && i.GetProperty("dy").GetDouble() == 1),
                    $"{mirror.Subtitle}: Win32 wheel reaches the correct media socket");
                Program.Check(SessionInputs().Count(i => i.TryGetProperty("code", out var code) && code.GetInt32() == 4) == beforeBack + 2 &&
                    PointerInputs().Count == beforeNavigation,
                    $"{mirror.Subtitle}: enabling right-click Back sends one Android Back pair without a long press");
                mirror.Shortcuts.RightClickBackEnabled = false;
                if (!mirror.IsPhone)
                {
                    var before = SessionInputs().Count;
                    server.RecoverTaskAfterBack = true;
                    MirrorCaptionChecks.ClickBack(window);
                    await UntilAsync(() => SessionInputs().Count >= before + 2, ct);
                    await Task.Delay(100, ct);
                    var keys = SessionInputs().Skip(before).ToArray();
                    Program.Check(keys.Length == 2 && keys.Select(key => key.GetProperty("a").GetString()).SequenceEqual(["down", "up"]) &&
                        keys.All(key => key.GetProperty("code").GetInt32() == 4 && key.GetProperty("meta").GetInt32() == 0) &&
                        !mirror.IsDisposed && window.IsVisible,
                        "native click on frame Back sends one Android Back pair without closing the window or leaking video pointer input");
                    server.RecoverTaskAfterBack = false;
                    MirrorCaptionChecks.Button(window, "PART_CloseButton").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                    await UntilAsync(() => mirror.IsDisposed && !window.IsVisible, ct);
                    Program.Check(true, "framework caption Close releases the mirror window and its session");
                }
            }

            List<System.Text.Json.JsonElement> AllSessionInputs() => server!.MediaInputs.Where(i => i.SessionId == mirror.SessionId).Select(i => i.Message).ToList();
            // Native capture/layout changes can generate real hover messages between keyboard assertions.
            List<System.Text.Json.JsonElement> SessionInputs() => AllSessionInputs().Where(i =>
                !i.TryGetProperty("a", out var action) || action.GetString() != "hover").ToList();
            List<System.Text.Json.JsonElement> PointerInputs() => SessionInputs().Where(i => i.TryGetProperty("k", out var kind) && kind.GetString() == "pointer").ToList();
        }
        finally { window.Close(); await mirror.DisposeAsync(); }
    }

    private static Point VideoPoint(VideoView surface, double x, double y)
    {
        var frame = surface.Frame!;
        var scale = Math.Min(surface.Bounds.Width / frame.Width, surface.Bounds.Height / frame.Height);
        return new((surface.Bounds.Width - frame.Width * scale) / 2 + frame.Width * scale * x,
            (surface.Bounds.Height - frame.Height * scale) / 2 + frame.Height * scale * y);
    }

    private static void Inject(MirrorWindow window, VideoView surface, Point point, uint message, nuint buttons)
    {
        var root = window.TryGetPlatformHandle()!.Handle;
        var screen = surface.PointToScreen(point);
        var target = GetCapture();
        if (target == 0) target = WindowFromPoint(new NativePoint(screen.X, screen.Y));
        if (target != root) throw new Exception($"Windows did not target the Avalonia top level for mirror input (target={target}, root={root}, capture={GetCapture()}).");
        var position = new NativePoint(screen.X, screen.Y);
        if (message != WmWheel) ScreenToClient(target, ref position);
        SendMessage(target, message, buttons, Pack(position.X, position.Y));
    }

    private static async Task UntilAsync(Func<bool> condition, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(25, timeout.Token);
    }

    private static nint Pack(int x, int y) => unchecked((nint)(int)((uint)(ushort)x | ((uint)(ushort)y << 16)));
    internal static void InjectKey(nint root, nuint key, KeyModifiers modifiers, char? character)
    {
        var saved = new byte[256];
        if (!GetKeyboardState(saved)) throw new IOException("Cannot read keyboard state.");
        try
        {
            var state = new byte[256];
            if (modifiers.HasFlag(KeyModifiers.Control)) state[0x11] = state[0xA2] = 0x80;
            if (modifiers.HasFlag(KeyModifiers.Alt)) state[0x12] = state[0xA4] = 0x80;
            if (modifiers.HasFlag(KeyModifiers.Shift)) state[0x10] = state[0xA0] = 0x80;
            // SetKeyboardState changes only this UI thread's input state, not the system keyboard.
            if (!SetKeyboardState(state)) throw new IOException("Cannot set test keyboard state.");
            SendMessage(root, 0x100, key, 1); // WM_KEYDOWN
            if (character is { } value) SendMessage(root, 0x102, value, 1); // WM_CHAR from TranslateMessage
            SendMessage(root, 0x101, key, unchecked((nint)0xC0000001)); // WM_KEYUP
        }
        finally { SetKeyboardState(saved); }
    }

}
