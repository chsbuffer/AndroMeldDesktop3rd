using System.Text.Json;
using Andromeld.Desktop.Services;
using Andromeld.Desktop.ViewModels;
using Andromeld.Desktop.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace Andromeld.Desktop.Tests;

internal static class AboutChecks
{
    public static async Task RunAsync(MainViewModel model, MainWindow window, string temporary, CancellationToken ct)
    {
        Program.Check(model.NeedsDevelopmentNotice && JsonSerializer.Deserialize<Settings>("{}") is { DevelopmentNoticeAcknowledged: false },
            "new and pre-existing settings without acknowledgement require the development notice");
        var pending = window.ShowDevelopmentNoticeAsync();
        await UntilAsync(() => Dialog() is not null, ct);
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        await pending.WaitAsync(ct);
        Program.Check(model.NeedsDevelopmentNotice, "Escape dismisses the notice without recording acknowledgement");

        pending = window.ShowDevelopmentNoticeAsync();
        await UntilAsync(() => Dialog() is not null, ct);
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var button = Dialog()!.GetVisualDescendants().OfType<Button>().Single(control => Equals(control.Content, "我知道了"));
        var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
        await pending.WaitAsync(ct);
        var settingsPath = Path.Combine(temporary, "settings.json");
        var restored = JsonSerializer.Deserialize<Settings>(File.ReadAllText(settingsPath))!;
        Program.Check(!model.NeedsDevelopmentNotice && restored.DevelopmentNoticeAcknowledged,
            "the actual confirmation button records acknowledgement in settings");

        await using (var restarted = new MainViewModel(new Settings { StoragePath = settingsPath, DevelopmentNoticeAcknowledged = restored.DevelopmentNoticeAcknowledged }, new StubDiscovery(), usbDiscovery: new StubUsbDiscovery()))
        {
            var nextWindow = new MainWindow { DataContext = restarted };
            await nextWindow.ShowDevelopmentNoticeAsync().WaitAsync(ct);
            Program.Check(!restarted.NeedsDevelopmentNotice && !nextWindow.GetVisualDescendants().OfType<DevelopmentNoticeDialog>().Any(),
                "a later launch using the saved acknowledgement does not show the notice");
            nextWindow.DataContext = null;
        }

        var blockedPath = Path.Combine(temporary, "blocked-settings");
        File.WriteAllText(blockedPath, "synthetic file blocking the settings directory");
        await using (var unsaved = new MainViewModel(new Settings { StoragePath = Path.Combine(blockedPath, "settings.json") }, new StubDiscovery(), usbDiscovery: new StubUsbDiscovery()))
        {
            try { unsaved.AcknowledgeDevelopmentNotice(); throw new Exception("Expected settings save to fail"); }
            catch (IOException) { }
            Program.Check(unsaved.NeedsDevelopmentNotice, "a failed settings save keeps the notice pending for the next launch");
        }

        DevelopmentNoticeDialog? Dialog() => window.GetVisualDescendants().OfType<DevelopmentNoticeDialog>().FirstOrDefault();
    }

    private static async Task UntilAsync(Func<bool> condition, CancellationToken ct)
    {
        while (!condition()) await Task.Delay(10, ct);
    }
}
