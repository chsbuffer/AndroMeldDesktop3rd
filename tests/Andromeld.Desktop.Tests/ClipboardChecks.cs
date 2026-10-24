using Andromeld.Desktop.ViewModels;
using Andromeld.Desktop.Views;
using Andromeld.Desktop.Services;
using Andromeld.Protocol;
using Andromeld.Testing;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using ReactiveUI.Primitives;

namespace Andromeld.Desktop.Tests;

internal static class ClipboardChecks
{
    public static async Task RunWindowsAsync(MainViewModel model, MainWindow main, MockPhone server, bool native, CancellationToken ct)
    {
        var focusTarget = native ? new Window { Title = "Synthetic external clipboard owner", Width = 240, Height = 160 } : null;
        var clipboard = main.Clipboard ?? throw new Exception("No platform clipboard");
        await UntilAsync(() => Task.FromResult(main.IsActive), ct);
        // Use the window coordinator throughout: injecting SetActiveClipboard would hide activation bugs.
        await UntilAsync(async () => await clipboard.TryGetTextAsync() == MockPhone.RemoteClipboard, ct);
        Program.Check(true, "initial main-window activation automatically delivers phone clipboard text");
        await CheckUploadAsync("synthetic main-window clipboard");
        await HideAsync(main);
        var inactiveClipboard = focusTarget?.Clipboard ?? clipboard;
        await inactiveClipboard.SetTextAsync("synthetic inactive clipboard");
        await server.PushClipboardAsync("synthetic deferred phone clipboard");
        await Task.Delay(650, ct);
        Program.Check(await inactiveClipboard.TryGetTextAsync() == "synthetic inactive clipboard" &&
            !server.ClipboardMessages.Any(m => m.Type == 2 && m.Message.GetProperty("text").GetString() == "synthetic inactive clipboard"),
            "no application window active defers clipboard reads and writes");
        main.Show();
        await UntilAsync(() => Task.FromResult(main.IsActive), ct);
        await UntilAsync(async () => await clipboard.TryGetTextAsync() == "synthetic deferred phone clipboard", ct);
        await CheckUploadAsync("synthetic reactivated clipboard");
        Program.Check(true, "main-window reactivation resumes both clipboard directions");

        await HideAsync(main);
        await model.OpenPhoneCommand.Execute().ToTask();
        await CheckWindowAsync(main.MirrorWindows.Single(), "phone mirror");
        model.SelectedApp = model.Apps.First(a => !a.App.Game);
        await model.OpenSelectedAppCommand.Execute().ToTask();
        await CheckWindowAsync(main.MirrorWindows.Single(w => w.DataContext is MirrorViewModel { IsPhone: false }), "application mirror");
        await model.ShowFilesCommand.Execute().ToTask();
        await CheckWindowAsync(main.FileWindow ?? throw new Exception("No file window"), "file browser");
        await model.OpenTerminalCommand.Execute().ToTask();
        await CheckWindowAsync(main.TerminalWindows.Single(), "terminal");
        foreach (var mirror in model.Mirrors.ToArray()) await mirror.DisposeAsync();
        main.FileWindow?.Close();
        foreach (var terminal in model.Terminals.ToArray()) await terminal.DisposeAsync();
        main.Show();
        await UntilAsync(() => Task.FromResult(main.IsActive), ct);
        focusTarget?.Close();
        await server.PushClipboardAsync(MockPhone.RemoteClipboard);

        async Task CheckWindowAsync(Window window, string name)
        {
            await UntilAsync(() => Task.FromResult(window.IsActive), ct);
            var clip = window.Clipboard ?? throw new Exception("No auxiliary clipboard");
            var incoming = "synthetic phone clipboard for " + name;
            await server.PushClipboardAsync(incoming);
            await UntilAsync(async () => await clip.TryGetTextAsync() == incoming, ct);
            await CheckUploadAsync("synthetic desktop clipboard from " + name, clip);
            Program.Check(true, name + " activation automatically resumes bidirectional clipboard synchronization");
            await HideAsync(window);
        }

        async Task CheckUploadAsync(string text, IClipboard? target = null)
        {
            await (target ?? clipboard).SetTextAsync(text);
            await UntilAsync(() => Task.FromResult(server.ClipboardMessages.Any(m => m.Type == 2 && m.Message.GetProperty("text").GetString() == text)), ct);
        }

        async Task HideAsync(Window window)
        {
            if (focusTarget is not null)
            {
                // Transfer focus while the old surface is mapped so Wayland can report deactivation.
                focusTarget.Hide();
                focusTarget.Show();
                await UntilAsync(() => Task.FromResult(focusTarget.IsActive), ct);
                await UntilAsync(() => Task.FromResult(!window.IsActive), ct);
            }
            window.Hide();
            await UntilAsync(() => Task.FromResult(!window.IsActive), ct);
        }
    }

    public static async Task RunLiveAsync(MainWindow window, CancellationToken ct)
    {
        var settings = Settings.Load();
        var address = settings.Address.Contains("://") ? settings.Address : $"ws://{settings.Address}";
        var endpoint = new UriBuilder(address) { Path = "/ws" }.Uri;
        settings.Tokens.TryGetValue(endpoint.AbsoluteUri, out var token);
        if (token is null) throw new Exception("Live clipboard check requires existing paired credentials.");
        await using var client = new AndromeldClient();
        await client.ConnectAsync(endpoint, settings.ClientId, token, null, ct);
        var app = (await client.GetAppsAsync(ct)).Single(a => a.PackageName == "com.android.settings" && a.UserId == 0);
        await using var session = await client.OpenMirrorAsync(app, ct, 720, 480);
        await session.AttachAsync(ct);
        var marker = "AndroMeld clipboard check " + Guid.NewGuid().ToString("N");
        var echo = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ClipboardReceived += text => { if (text == marker) echo.TrySetResult(); };
        Exception? error = null;
        await using var sharing = new ClipboardSharing(client, ex => error = ex, ct);
        await sharing.StartAsync();
        await client.SetClipboardTextAsync(marker, ct);
        await echo.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        Program.Check(true, "real phone applies synthetic text and reports clipboard update on [3,1]");
        var clipboard = window.Clipboard ?? throw new Exception("No native clipboard");
        sharing.SetActiveClipboard(clipboard);
        await UntilAsync(async () => await clipboard.TryGetTextAsync() == marker, ct);
        Program.Check(error is null, "real phone clipboard reaches native platform clipboard through shared Avalonia service");
        Console.WriteLine("Clipboard contents and credentials were not logged or saved.");
    }

    public static async Task RunAsync(MainViewModel model, MainWindow window, MockPhone server, CancellationToken ct, bool native = false)
    {
        if (native)
        {
            window.FileWindow?.Close();
            window.Activate();
            await UntilAsync(() => Task.FromResult(window.IsActive), ct);
        }

        var clipboard = window.Clipboard ?? throw new Exception("No platform clipboard");
        model.SetActiveClipboard(clipboard);
        await UntilAsync(async () => await clipboard.TryGetTextAsync() == MockPhone.RemoteClipboard, ct);
        Program.Check(true, "phone clipboard is written through Avalonia IClipboard");
        const string local = "桌面共享\nUnicode ✓";
        await clipboard.SetTextAsync(local);
        await UntilAsync(() => Task.FromResult(server.ClipboardMessages.Any(m => m.Type == 2 && m.Message.GetProperty("text").GetString() == local)), ct);
        await Task.Delay(650, ct);
        Program.Check(server.ClipboardMessages.Count(m => m.Type == 2 && m.Message.GetProperty("text").GetString() == local) == 1,
            "desktop clipboard sends once and ignores the phone echo");
        model.SetActiveClipboard(null);
        await clipboard.SetTextAsync("inactive local text");
        await server.PushClipboardAsync("older phone text");
        await server.PushClipboardAsync("latest phone text");
        await Task.Delay(650, ct);
        Program.Check(await clipboard.TryGetTextAsync() == "inactive local text", "inactive application defers phone clipboard writes");
        model.SetActiveClipboard(clipboard);
        await UntilAsync(async () => await clipboard.TryGetTextAsync() == "latest phone text", ct);
        await Task.Delay(650, ct);
        Program.Check(!server.ClipboardMessages.Any(m => m.Type == 2 && m.Message.GetProperty("text").GetString() == "latest phone text"),
            "focus restores only the newest pending phone text without feeding it back");
        model.SetActiveClipboard(null);
    }

    private static async Task UntilAsync(Func<Task<bool>> predicate, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(6));
        while (!await predicate()) await Task.Delay(25, timeout.Token);
    }
}
