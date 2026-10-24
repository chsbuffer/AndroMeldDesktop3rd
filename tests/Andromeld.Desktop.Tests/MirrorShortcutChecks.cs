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
using ReactiveUI.Primitives;

namespace Andromeld.Desktop.Tests;

internal static class MirrorShortcutChecks
{
    public static async Task RunAsync(MainViewModel model, MainWindow main, MockPhone server, string settingsPath, CancellationToken ct)
    {
        server.RecoverTaskAfterBack = true;
        await model.OpenPhoneCommand.Execute().ToTask();
        var phone = model.Mirrors.Single();
        var window = main.MirrorWindows.Single();
        await UntilAsync(() => phone.HasFrame, ct);
        window.UpdateLayout();
        var screen = window.FindControl<VideoView>("Screen")!;
        var rail = window.FindControl<StackPanel>("NavigationRail")!;
        var buttons = rail.Children.Cast<Button>().ToArray();
        main.ShowSettings();
        var settingsPage = main.FindControl<SettingsView>("SettingsPage")!;
        var modifiers = settingsPage.FindControl<ComboBox>("MirrorModifierList")!;
        var rightClickBack = settingsPage.FindControl<ToggleSwitch>("MirrorRightClickBackToggle")!;
        Program.Check(rightClickBack.IsChecked == false && !model.MirrorShortcuts.RightClickBackEnabled,
            "right-click Back defaults off for settings without the new preference");
        await MirrorPointerChecks.BackSettingAsync(window, phone, server, rightClickBack, settingsPath, ct);
        await MirrorPointerChecks.RunAsync(window, phone, server, ct);
        foreach (var option in model.MirrorShortcuts.Options)
        {
            // Change through the actual settings control, then exercise the open mirror's key route.
            modifiers.SelectedItem = option;
            window.UpdateLayout();
            screen.Focus();
            await KeyPairAsync(Key.R, RawModifiers(option.Modifiers), 187);
            await KeyPairAsync(Key.H, RawModifiers(option.Modifiers), 3);
            var restored = JsonSerializer.Deserialize<Settings>(File.ReadAllText(settingsPath))!;
            Program.Check(ToolTip.GetTip(buttons[1])?.ToString() == $"最近任务（{option.Label}+R）" &&
                ToolTip.GetTip(buttons[2])?.ToString() == $"主页（{option.Label}+H）" &&
                restored.MirrorModifiers == option.Modifiers && restored.Tokens.Values.Contains("test-token"),
                $"{option.Label}: settings persist, open-window hints update, R/H send isolated navigation key pairs");
        }
        // Extra modifiers must not accidentally invoke a configured shortcut.
        modifiers.SelectedItem = model.MirrorShortcuts.Options[0];
        screen.Focus();
        var before = Inputs(phone).Count;
        PressAndRelease(window, Key.R, RawInputModifiers.Control | RawInputModifiers.Shift);
        await UntilAsync(() => Inputs(phone).Count >= before + 2, ct);
        var ordinary = Inputs(phone).Skip(before).ToArray();
        Program.Check(ordinary.Length == 2 && ordinary.All(i => i.GetProperty("code").GetInt32() == 46 && i.GetProperty("meta").GetInt32() == 4097),
            "nonmatching modifier combination remains a remote R key with its original modifiers");
        buttons[0].Focus();
        await KeyPairAsync(Key.Escape, RawInputModifiers.None, 4);
        Program.Check(true, "Escape returns from the phone even when a navigation button has focus");
        before = Inputs(phone).Count;
        buttons[0].Focus();
        PressAndRelease(window, Key.Space);
        await UntilAsync(() => Inputs(phone).Count >= before + 2, ct);
        Program.Check(Inputs(phone).Skip(before).All(i => i.GetProperty("code").GetInt32() == 26), "Power button sends Android power down/up");
        await MirrorPointerChecks.ClosePendingAsync(window, phone, server, ct);
        await phone.DisposeAsync();
        model.SelectedPage = WorkspacePage.Applications;

        model.SelectedApp = model.Apps.First(a => !a.App.Game);
        await model.OpenSelectedAppCommand.Execute().ToTask();
        var application = model.Mirrors.Single();
        var appWindow = main.MirrorWindows.Single();
        await UntilAsync(() => application.HasFrame, ct);
        appWindow.UpdateLayout();
        var appScreen = appWindow.FindControl<VideoView>("Screen")!;
        await MirrorPointerChecks.BackSettingAsync(appWindow, application, server, rightClickBack, settingsPath, ct);
        await MirrorPointerChecks.RunAsync(appWindow, application, server, ct);
        appScreen.Focus();
        before = Inputs(application).Count;
        PressAndRelease(appWindow, Key.H, RawInputModifiers.Control);
        PressAndRelease(appWindow, Key.R, RawInputModifiers.Control);
        await UntilAsync(() => Inputs(application).Count >= before + 4, ct);
        Program.Check(Inputs(application).Skip(before).All(i => i.GetProperty("code").GetInt32() is 36 or 46),
            "application H/R combinations remain app input instead of phone navigation");
        server.CloseWindowOnBack = true;
        server.RecoverTaskAfterBack = false;
        var appSessionId = application.SessionId;
        PressAndRelease(appWindow, Key.Escape);
        await UntilAsync(() => application.IsDisposed && main.MirrorWindows.Count == 0, ct);
        Program.Check(server.MediaInputs.Any(i => i.SessionId == appSessionId && i.Message.TryGetProperty("code", out var code) && code.GetInt32() == 4) && main.MirrorWindows.Count == 0,
            "application Escape sends Back and follows server window-state.closed");
        server.CloseWindowOnBack = false;

        List<JsonElement> Inputs(MirrorViewModel mirror) => server.MediaInputs.Where(i => i.SessionId == mirror.SessionId).Select(i => i.Message).ToList();
        async Task KeyPairAsync(Key key, RawInputModifiers modifier, int expected)
        {
            var count = Inputs(phone).Count;
            PressAndRelease(window, key, modifier);
            await UntilAsync(() => Inputs(phone).Count >= count + 2, ct);
            var input = Inputs(phone).Skip(count).ToArray();
            if (input.Length != 2 || !input.Select(i => i.GetProperty("a").GetString()).SequenceEqual(["down", "up"]) ||
                !input.All(i => i.GetProperty("code").GetInt32() == expected && i.GetProperty("meta").GetInt32() == 0))
                throw new Exception($"Incorrect mirror shortcut input: {modifier}+{key}");
        }
    }

    private static void PressAndRelease(Window window, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        var physicalKey = Enum.Parse<PhysicalKey>(key.ToString());
        window.KeyPress(key, modifiers, physicalKey, null);
        window.KeyRelease(key, modifiers, physicalKey, null);
    }

    private static RawInputModifiers RawModifiers(KeyModifiers value)
    {
        var raw = RawInputModifiers.None;
        if (value.HasFlag(KeyModifiers.Control)) raw |= RawInputModifiers.Control;
        if (value.HasFlag(KeyModifiers.Alt)) raw |= RawInputModifiers.Alt;
        if (value.HasFlag(KeyModifiers.Shift)) raw |= RawInputModifiers.Shift;
        return raw;
    }

    private static async Task UntilAsync(Func<bool> condition, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }
}
