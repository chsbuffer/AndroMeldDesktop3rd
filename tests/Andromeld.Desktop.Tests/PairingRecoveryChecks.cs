using System.Text.Json;
using Andromeld.Desktop.Controls;
using Andromeld.Desktop.Services;
using Andromeld.Desktop.ViewModels;
using Andromeld.Desktop.Views;
using Andromeld.Protocol;
using Andromeld.Testing;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using ReactiveUI.Primitives;

namespace Andromeld.Desktop.Tests;

internal static class PairingRecoveryChecks
{
    public static async Task RunAsync(string temporary, CancellationToken ct)
    {
        await CheckCardAsync(temporary, ct);
        await CheckCancellationAsync(temporary, ct);
        foreach (var reason in new[] { "BUSY", "DENIED", "TIMEOUT" })
            await CheckDenialAsync(temporary, "resume-" + reason, "test-token", reason, null, reason, ["resume"], ct);
        await CheckDenialAsync(temporary, "pair-denied", "expired-token", null, "DENIED", "DENIED", ["resume", "pair"], ct);
        await CheckDenialAsync(temporary, "pair-unauthorized", null, null, "UNAUTHORIZED", "UNAUTHORIZED", ["pair"], ct);
    }

    private static Settings CreateSettings(MockPhone phone, string temporary, string name, string? token)
    {
        var settings = new Settings { StoragePath = Path.Combine(temporary, "pairing", name, "settings.json"), Address = phone.Endpoint.Authority };
        if (token is not null) settings.Tokens[phone.Endpoint.AbsoluteUri] = token;
        settings.Save();
        return settings;
    }

    private static async Task CheckCardAsync(string temporary, CancellationToken ct)
    {
        await using var phone = await MockPhone.StartAsync("pairing-recovery");
        phone.PairingReplyGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var settings = CreateSettings(phone, temporary, "card", "expired-token");
        await using var model = new MainViewModel(settings, new StubDiscovery(), usbDiscovery: new StubUsbDiscovery()) { Code = "1955-JNBT-WANX" };
        var window = new MainWindow { DataContext = model };
        window.Show();
        try
        {
            window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            var page = window.FindControl<DeviceConnectionView>("DevicePage")!;
            var card = page.FindControl<ItemsControl>("NearbyDeviceCards")!.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("nearbyDevice"));
            var point = card.TranslatePoint(new Point(card.Bounds.Width / 2, card.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
            window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
            await UntilAsync(() => model.Status.StartsWith("配对码", StringComparison.Ordinal), ct);
            var persisted = JsonSerializer.Deserialize<Settings>(File.ReadAllText(settings.StoragePath))!;
            Program.Check(phone.AuthenticationTypes.SequenceEqual(["resume", "pair"]) && model.IsConnecting && !model.IsConnected &&
                !model.CanSelectNearbyDevice && !persisted.Tokens.ContainsKey(phone.Endpoint.AbsoluteUri) && !model.HasError,
                "one card click retires a rejected resume credential, persists its removal and waits for phone approval on a fresh socket");
            phone.PairingReplyGate.SetResult();
            await UntilAsync(() => model.IsConnected && model.HasDeviceStorage && !model.IsConnecting, ct);
            persisted = JsonSerializer.Deserialize<Settings>(File.ReadAllText(settings.StoragePath))!;
            Program.Check(persisted.Tokens[phone.Endpoint.AbsoluteUri] == "test-token" && model.Code == "1955-JNBT-WANX" && !model.HasError,
                "phone approval saves the replacement credential without consuming the separate connection code or disconnecting from stale callbacks");
            await model.DisconnectCommand.Execute().ToTask();
            await model.ConnectCommand.Execute().ToTask();
            Program.Check(model.IsConnected && phone.AuthenticationTypes.SequenceEqual(["resume", "pair", "resume"]),
                "the next connection resumes the replacement pairing without another approval request");
        }
        finally
        {
            phone.PairingReplyGate.TrySetResult();
            window.DataContext = null; window.Close();
        }
    }

    private static async Task CheckCancellationAsync(string temporary, CancellationToken ct)
    {
        await using var phone = await MockPhone.StartAsync("pairing-cancel");
        phone.PairingReplyGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var settings = CreateSettings(phone, temporary, "cancel", "expired-token");
        await using var model = new MainViewModel(settings, new StubDiscovery(), usbDiscovery: new StubUsbDiscovery());
        var pending = model.ConnectCommand.Execute().ToTask();
        try
        {
            await UntilAsync(() => model.Status.StartsWith("配对码", StringComparison.Ordinal), ct);
            await model.CancelConnectCommand.Execute().ToTask();
            await pending;
            phone.PairingReplyGate.SetResult();
            await Task.Delay(50, ct);
            Program.Check(!model.IsConnected && !model.IsConnecting && model.CanConnect && model.Status == "已取消连接" &&
                !settings.Tokens.ContainsKey(phone.Endpoint.AbsoluteUri) && phone.AuthenticationTypes.SequenceEqual(["resume", "pair"]),
                "Cancel stops the replacement pairing, restores connection actions and rejects a late welcome");
        }
        finally { phone.PairingReplyGate.TrySetResult(); }
    }

    private static async Task CheckDenialAsync(string temporary, string name, string? token, string? resumeReason, string? pairReason, string expected, string[] attempts, CancellationToken ct)
    {
        await using var phone = await MockPhone.StartAsync(name);
        phone.ResumeDenialReason = resumeReason;
        phone.PairDenialReason = pairReason;
        var settings = CreateSettings(phone, temporary, name, token);
        await using var model = new MainViewModel(settings, new StubDiscovery(), usbDiscovery: new StubUsbDiscovery());
        try { await model.ConnectCommand.Execute().ToTask(); throw new Exception("Expected denial"); }
        catch (AuthenticationDeniedException ex)
        {
            // The generated command publishes its error on the UI scheduler before the owner is disposed.
            await UntilAsync(() => model.HasError, ct);
            Program.Check(ex.Reason == expected && phone.AuthenticationTypes.SequenceEqual(attempts) && !model.IsConnecting && !model.IsConnected &&
                (resumeReason is null || settings.Tokens[phone.Endpoint.AbsoluteUri] == token),
                $"{name}: denial remains visible without extra pairing attempts or removal of an unrevoked resume credential");
        }
    }

    private static async Task UntilAsync(Func<bool> ready, CancellationToken ct)
    {
        while (!ready()) await Task.Delay(10, ct);
    }
}
