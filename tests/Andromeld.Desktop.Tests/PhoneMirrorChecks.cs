using Andromeld.Desktop.ViewModels;
using Andromeld.Desktop.Views;
using Andromeld.Testing;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using ReactiveUI.Primitives;

namespace Andromeld.Desktop.Tests;

internal static class PhoneMirrorChecks
{
    public static async Task RunAsync(MainViewModel model, MainWindow main, MockPhone server, CancellationToken ct)
    {
        await model.OpenPhoneCommand.Execute().ToTask();
        var phone = model.Mirrors.Single();
        var window = main.MirrorWindows.Single();
        await UntilAsync(() => phone.HasFrame, ct);
        var fusion = window.FindControl<Button>("FusionModeButton")!;
        var power = window.FindControl<Button>("DisplayPowerButton")!;
        await UntilAsync(() => power.IsEffectivelyEnabled && !fusion.IsEffectivelyEnabled, ct);
        Program.Check(!fusion.IsEffectivelyEnabled && power.IsEffectivelyEnabled && !phone.IsScreenOff,
            "phone fusion waits for a foreground application while display power is available after attach");

        Click(window, power);
        await UntilAsync(() => PowerRequests().Length == 1, ct);
        Program.Check(phone.IsScreenOff && phone.CanControl && phone.ShowVideo &&
            ToolTip.GetTip(power)?.ToString() == "点亮屏幕" && !PowerRequests()[0].Message.GetProperty("on").GetBoolean(),
            "actual phone power button immediately selects screen-off and keeps video/input without waiting for an echo");
        foreach (var (state, reason) in new[] { (1, 3), (2, 4) })
        {
            await server.PushDisplayPowerStateAsync(phone.SessionId!, state, reason);
            // The following ordered foreground notification is a presentation barrier on this media reader.
            var marker = $"synthetic.state.{state}.{reason}";
            await server.PushFocusedApplicationAsync(phone.SessionId!, marker);
            await UntilAsync(() => phone.FocusedApplication?.PackageName == marker, ct);
            Program.Check(phone.IsScreenOff, $"display-power-state {state}/{reason} does not reset screen-off");
        }
        await server.PushDisplayPowerStateAsync(phone.SessionId!, 1, 4);
        await UntilAsync(() => !phone.IsScreenOff, ct);
        Program.Check(ToolTip.GetTip(power)?.ToString() == "关屏使用", "only state=1/reason=4 resets the local screen-off selection");
        Click(window, power);
        await UntilAsync(() => PowerRequests().Length == 2, ct);
        Click(window, power);
        await UntilAsync(() => PowerRequests().Length == 3, ct);
        Program.Check(!phone.IsScreenOff && PowerRequests().Select(i => i.Message.GetProperty("on").GetBoolean()).SequenceEqual([false, false, true]),
            "power button sends the correct off/on direction after a device state reset");

        await server.PushFocusedApplicationAsync(phone.SessionId!, "test.app0");
        await UntilAsync(() => phone.FocusedApplication?.PackageName == "test.app0" && fusion.IsEffectivelyEnabled, ct);
        Click(window, fusion);
        await UntilAsync(() => model.Mirrors.Count == 2 && model.Mirrors.All(m => m.CanControl), ct);
        var application = model.Mirrors.Single(m => !m.IsPhone);
        var opened = server.OpenedMirrors.Count;
        MirrorViewModel? activated = null;
        Task ObserveFocus(MirrorViewModel mirror) { activated = mirror; return Task.CompletedTask; }
        model.MirrorRequested += ObserveFocus;
        await phone.EnterFusionModeCommand.Execute().ToTask();
        model.MirrorRequested -= ObserveFocus;
        Program.Check(application.App?.PackageName == "test.app0" && activated == application && server.OpenedMirrors.Count == opened &&
            model.Mirrors.Contains(phone) && !phone.IsDisposed && main.MirrorWindows.Count == 2 &&
            !((System.Windows.Input.ICommand)application.ToggleDisplayPowerCommand).CanExecute(null),
            "fusion opens then focuses one independent application mirror while retaining the phone window");

        await server.PushFocusedApplicationAsync(phone.SessionId!, "test.app17", 0);
        await UntilAsync(() => phone.FocusedApplication?.PackageName == "test.app17", ct);
        await phone.EnterFusionModeCommand.Execute().ToTask();
        Program.Check(model.Mirrors.Count == 2 && phone.CanControl && phone.Error.Contains("请先"),
            "fusion refuses a package from a different Android user without stopping the phone mirror");
        await server.PushFocusedApplicationAsync(phone.SessionId!, "test.app17", 10);
        await UntilAsync(() => phone.FocusedApplication?.UserId == 10, ct);
        await server.PushFocusedApplicationAsync(application.SessionId!, "test.app17", 10);
        await UntilAsync(() => application.FocusedApplication?.PackageName == "test.app17", ct);
        await phone.EnterFusionModeCommand.Execute().ToTask();
        Program.Check(model.Mirrors.Count == 2 && server.OpenedMirrors.Count == opened,
            "fusion also reuses an application window whose current foreground target matches");
        await server.PushFocusedApplicationAsync(application.SessionId!, null);
        await UntilAsync(() => application.FocusedApplication?.PackageName is null, ct);
        await phone.EnterFusionModeCommand.Execute().ToTask();
        Program.Check(model.Mirrors.Single(m => m.App?.PackageName == "test.app17").App?.UserId == 10,
            "fusion opens the exact foreground Android user");

        await server.PushFocusedApplicationAsync(phone.SessionId!, null);
        await UntilAsync(() => !phone.CanEnterFusionMode && !fusion.IsEffectivelyEnabled, ct);
        foreach (var mirror in model.Mirrors.ToArray()) await mirror.DisposeAsync();
        var count = PowerRequests().Length;
        server.InitialFocusedApplication = new("test.app17", 10);
        server.InitiallyScreenOff = true;
        await model.OpenPhoneCommand.Execute().ToTask();
        phone = model.Mirrors.Single();
        await UntilAsync(() => phone.CanControl && phone.IsScreenOff && phone.FocusedApplication?.UserId == 10, ct);
        Program.Check(phone.CanEnterFusionMode && PowerRequests().Length == count,
            "attach restores the held screen-off and focused application without resending display power");
        await phone.DisposeAsync();
        Program.Check(PowerRequests().Length == count && main.MirrorWindows.Count == 0,
            "closing the phone releases its window without an extra display power command");
        server.InitialFocusedApplication = null;
        server.InitiallyScreenOff = false;

        (string SessionId, System.Text.Json.JsonElement Message)[] PowerRequests() =>
            server.MediaInputs.Where(i => i.Message.GetProperty("c").GetString() == "display-power").ToArray();
    }

    private static void Click(Window window, Button button)
    {
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
    }

    private static async Task UntilAsync(Func<bool> condition, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
}
