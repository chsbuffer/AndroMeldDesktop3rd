using System.Text.Json;
using Andromeld.Desktop.Services;
using Andromeld.Desktop.ViewModels;
using Andromeld.Desktop.Views;
using Andromeld.Protocol;
using Andromeld.Testing;
using ReactiveUI.Primitives;

namespace Andromeld.Desktop.Tests;

internal static class MirrorDisplayChecks
{
    internal static async Task RunAsync(MainViewModel model, MainWindow main, MockPhone server, CancellationToken ct, bool native = false)
    {
        var mirror = model.Mirrors.Single();
        Program.Check(!mirror.ImmersiveTitleBarEnabled, "games default to a titlebar outside the video");
        var initial = mirror.DisplayConfiguration;
        await UntilAsync(() => HasDisplay(initial), ct);
        await mirror.ZoomInCommand.Execute().ToTask();
        await UntilAsync(() => HasDisplay(mirror.DisplayConfiguration), ct);
        Program.Check(mirror.ZoomPercent == 110 && mirror.DisplayConfiguration.Size == initial.Size &&
            mirror.DisplayConfiguration.Scale != initial.Scale, "menu zoom changes Android density while preserving viewport size");
        for (var i = 0; i < 4; i++) await mirror.ZoomInCommand.Execute().ToTask();
        Program.Check(mirror.ZoomPercent == 150 && !mirror.CanZoomIn, "zoom stops at 150 percent");
        for (var i = 0; i < 10; i++) await mirror.ZoomOutCommand.Execute().ToTask();
        Program.Check(mirror.ZoomPercent == 50 && !mirror.CanZoomOut, "zoom stops at 50 percent");
        for (var i = 0; i < 5; i++) await mirror.ZoomInCommand.Execute().ToTask();
        await mirror.CompatibilityAllCommand.Execute().ToTask();
        await UntilAsync(() => HasDisplay(mirror.DisplayConfiguration), ct);
        Program.Check(mirror.IsCompatibilityAll && mirror.DisplayConfiguration.Size != initial.Size,
            "compatibility menu reconfigures the existing virtual display");
        model.SelectedApp = model.Apps.First(tile => tile.App.PackageName != mirror.App!.PackageName && !tile.App.Game);
        await model.OpenSelectedAppCommand.Execute().ToTask();
        var other = model.Mirrors.Single(item => item != mirror);
        Program.Check(other.ImmersiveTitleBarEnabled, "ordinary applications default to an immersive titlebar");
        var open = server.OpenedMirrors.Last();
        Program.Check(other.IsCompatibilityAll && open.GetProperty("w").GetInt32() == other.DisplayConfiguration.Size.Width &&
            open.GetProperty("h").GetInt32() == other.DisplayConfiguration.Size.Height,
            "all-app compatibility applies to the initial dimensions of newly opened mirrors");
        if (native && OperatingSystem.IsLinux())
            await LinuxMirrorDecorationChecks.RunAsync(main.MirrorWindows.Single(window => window.DataContext == other), other, ct);
        await mirror.CompatibilityAppCommand.Execute().ToTask();
        Program.Check(mirror.IsCompatibilityApp && other.IsCompatibilityOff,
            "switching All to this app updates other open windows immediately");
        await mirror.CompatibilityOffCommand.Execute().ToTask();
        await UntilAsync(() => HasDisplay(initial), ct);
        await other.DisposeAsync();
        Program.Check(main.MirrorWindows.Count == 1 && mirror.IsCompatibilityOff && mirror.ZoomPercent == 100,
            "display changes preserve the original mirror session and return to its ordinary geometry");
        await CheckPersistenceAsync(mirror.App!, ct);

        bool HasDisplay(MirrorDisplayConfiguration expected)
        {
            var messages = server.MediaInputs.Where(i => i.SessionId == mirror.SessionId).Select(i => i.Message).ToArray();
            var density = messages.LastOrDefault(i => i.GetProperty("c").GetString() == "density");
            var resize = messages.LastOrDefault(i => i.GetProperty("c").GetString() == "resize");
            return density.ValueKind != JsonValueKind.Undefined && resize.ValueKind != JsonValueKind.Undefined &&
                density.GetProperty("scale").GetDouble() == expected.Scale &&
                resize.GetProperty("w").GetInt32() == expected.Size.Width && resize.GetProperty("h").GetInt32() == expected.Size.Height &&
                resize.GetProperty("dpr").GetDouble() == expected.Size.Dpr;
        }
    }

    private static async Task CheckPersistenceAsync(PhoneApp app, CancellationToken ct)
    {
        var directory = Path.Combine(Path.GetTempPath(), "andromeld-display-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json");
        try
        {
            var settings = new Settings { StoragePath = path };
            var preferences = new MirrorDisplayPreferences(settings);
            var ordinary = app with { PackageName = "ordinary.package", Game = false };
            Program.Check(!preferences.GetScreenCamera("device-a", app) && preferences.GetWindow("device-a", app) is null,
                "new applications default to camera off and have no saved window size");
            preferences.SetScreenCamera("device-a", app, true);
            preferences.SetScreenCamera("device-a", ordinary, false);
            var savedWindow = new MirrorWindowSettings(720, 560, false);
            preferences.SetWindow("device-a", app, savedWindow);
            preferences.SetWindow("device-a", ordinary, new(480, 800, true));
            Program.Check(!preferences.GetImmersiveTitleBar("device-a", app) && preferences.GetImmersiveTitleBar("device-a", ordinary),
                "absent titlebar preferences use game and ordinary-app defaults");
            preferences.SetImmersiveTitleBar("device-a", app, true);
            preferences.SetImmersiveTitleBar("device-a", ordinary, false);
            preferences.SetZoom("device-a", app, 120);
            preferences.SetCompatibility("device-a", app.PackageName, MirrorCompatibility.App);
            preferences.SetCompatibility("device-a", "other.package", MirrorCompatibility.App);
            preferences.SetCompatibility("device-a", app.PackageName, MirrorCompatibility.All);
            preferences.SetCompatibility("device-a", app.PackageName, MirrorCompatibility.Off);
            var restored = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path))!;
            var read = new MirrorDisplayPreferences(restored);
            Program.Check(read.GetScreenCamera("device-a", app) && !read.GetScreenCamera("device-a", ordinary) &&
                !read.GetScreenCamera("device-b", app) && !read.GetScreenCamera("device-a", app with { UserId = app.UserId + 10 }) &&
                read.GetWindow("device-a", app) == savedWindow && read.GetWindow("device-a", ordinary) is { Maximized: true } &&
                read.GetWindow("device-b", app) is null && read.GetWindow("device-a", app with { UserId = app.UserId + 10 }) is null,
                "camera choices and window size/state survive JSON and isolate applications, devices and Android users");
            var legacy = JsonSerializer.Deserialize<Settings>("""{"MirrorDisplays":{"device-a":{"ZoomPercent":{"package:0":120}}}}""")!;
            var legacyRead = new MirrorDisplayPreferences(legacy);
            Program.Check(!legacyRead.GetScreenCamera("device-a", app) && legacyRead.GetWindow("device-a", app) is null,
                "older display settings load without camera or window fields");
            restored.MirrorDisplays["device-a"].Windows[app.PackageName + ":" + app.UserId] = new(-1, 560, false);
            Program.Check(read.GetWindow("device-a", app) is null, "invalid saved window sizes fall back to the ordinary first-open policy");
            Program.Check(read.GetImmersiveTitleBar("device-a", app) && !read.GetImmersiveTitleBar("device-a", ordinary) &&
                !read.GetImmersiveTitleBar("device-b", app) && read.GetImmersiveTitleBar("device-b", ordinary) &&
                !read.GetImmersiveTitleBar("device-a", app with { UserId = app.UserId + 10 }) &&
                read.GetImmersiveTitleBar("device-a", ordinary with { UserId = ordinary.UserId + 10 }),
                "explicit on and off titlebar choices survive JSON and isolate applications, devices and Android users");
            Program.Check(read.GetZoom("device-a", app) == 120 && read.GetZoom("device-a", app with { UserId = app.UserId + 10 }) == 100 &&
                read.GetZoom("device-b", app) == 100 && read.GetCompatibility("device-b", "other.package") == MirrorCompatibility.Off &&
                read.GetCompatibility("device-a", app.PackageName) == MirrorCompatibility.Off &&
                read.GetCompatibility("device-a", "other.package") == MirrorCompatibility.App,
                "display preferences survive JSON, isolate device/user zoom and retain other packages when leaving All");
            preferences.SetCompatibility("device-a", app.PackageName, MirrorCompatibility.App);
            Program.Check(preferences.GetCompatibility("device-a", (app with { UserId = app.UserId + 10 }).PackageName) == MirrorCompatibility.App,
                "compatibility is shared by package across Android users, matching Web");
            Program.Check(await Task.Run(() =>
            {
                try { preferences.GetZoom("device-a", app); return false; }
                catch (InvalidOperationException) { return true; }
            }, ct), "display preferences reject access from a non-owner thread");
            var blocked = Path.Combine(directory, "blocked");
            File.WriteAllText(blocked, "synthetic");
            var unsaved = new MirrorDisplayPreferences(new Settings { StoragePath = Path.Combine(blocked, "settings.json") });
            var notified = false;
            unsaved.Changed += (_, _) => notified = true;
            try { unsaved.SetZoom("device-a", app, 130); throw new Exception("Save failure ignored"); }
            catch (IOException)
            {
                Program.Check(notified && unsaved.GetZoom("device-a", app) == 130,
                    "failed persistence still notifies windows and preserves the effective in-memory display setting");
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static async Task UntilAsync(Func<bool> condition, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(25, timeout.Token);
    }
}
