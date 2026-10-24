using System.Text.Json;
using Andromeld.Desktop.Services;
using Andromeld.Desktop.ViewModels;
using Andromeld.Desktop.Views;
using Andromeld.Protocol;
using Andromeld.Testing;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using ReactiveUI.Primitives;
using SDL = SDL3.SDL;

namespace Andromeld.Desktop.Tests;

internal static class SharingChecks
{
    internal sealed class Playback : IAudioPlayback
    {
        public AudioConfiguration? Configuration { get; private set; }
        public List<byte[]> Writes { get; } = [];
        public bool Disposed { get; private set; }
        public void Configure(AudioConfiguration configuration) { Dispatcher.UIThread.VerifyAccess(); Configuration = configuration; }
        public void Write(ReadOnlySpan<byte> pcm) { Dispatcher.UIThread.VerifyAccess(); Writes.Add(pcm.ToArray()); }
        public void Dispose() { Dispatcher.UIThread.VerifyAccess(); Disposed = true; }
    }

    public static async Task RunAsync(MainViewModel model, MainWindow window, MockPhone phone, List<Playback> players, string settingsPath, CancellationToken ct)
    {
        var legacy = JsonSerializer.Deserialize<Settings>("{}")!;
        Program.Check(legacy.AudioForwardingEnabled && legacy.ClipboardSyncEnabled, "new and legacy settings default audio and clipboard to enabled");
        window.ShowSettings(); window.UpdateLayout();
        var page = window.FindControl<SettingsView>("SettingsPage")!;
        var audioToggle = page.FindControl<ToggleSwitch>("AudioForwardingToggle")!;
        var clipboardToggle = page.FindControl<ToggleSwitch>("ClipboardSyncToggle")!;
        Program.Check(audioToggle.IsChecked == true && clipboardToggle.IsChecked == true, "main settings page exposes both enabled synchronization switches");

        var clip = window.Clipboard!;
        model.SetActiveClipboard(clip);
        clipboardToggle.IsChecked = false;
        await UntilAsync(() => phone.ClipboardMessages.Last().Type == 3 && !phone.ClipboardMessages.Last().Message.GetProperty("on").GetBoolean(), ct);
        var before = phone.ClipboardMessages.Count(m => m.Type == 2);
        await clip.SetTextAsync("disabled clipboard synthetic text");
        await Task.Delay(650, ct);
        Program.Check(phone.ClipboardMessages.Count(m => m.Type == 2) == before, "clipboard switch unsubscribes and stops polling/sending local text");

        await model.OpenPhoneCommand.Execute().ToTask();
        var first = model.Mirrors.Single();
        await UntilAsync(() => players.Any(p => p.Writes.Count > 0), ct);
        var player = players.Last();
        Program.Check(player.Configuration?.Channels == 1 && player.Writes.Last().SequenceEqual(MockPhone.SyntheticPcm), "phone mirror plays typed S16LE audio through the UI-owned output");
        var onCount = phone.AudioSubscriptions.Count(m => m.GetProperty("on").GetBoolean());
        await model.OpenPhoneCommand.Execute().ToTask();
        Program.Check(phone.AudioSubscriptions.Count(m => m.GetProperty("on").GetBoolean()) == onCount, "activating the same mirror does not create duplicate audio subscriptions");

        audioToggle.IsChecked = false;
        Program.Check(player.Disposed, "audio switch immediately releases queued local playback");
        await UntilAsync(() => !phone.AudioSubscriptions.Last().GetProperty("on").GetBoolean(), ct);
        var saved = JsonSerializer.Deserialize<Settings>(File.ReadAllText(settingsPath))!;
        Program.Check(!saved.AudioForwardingEnabled && !saved.ClipboardSyncEnabled, "both disabled settings persist in the existing settings JSON");
        await first.DisposeAsync();
        await model.DisconnectCommand.Execute().ToTask();
        phone.AudioSubscriptions.Clear(); phone.ClipboardMessages.Clear();
        await model.ConnectCommand.Execute().ToTask();
        await model.OpenPhoneCommand.Execute().ToTask();
        await Task.Delay(100, ct);
        Program.Check(!phone.AudioSubscriptions.Any(m => m.GetProperty("on").GetBoolean()) && !phone.ClipboardMessages.Any(m => m.Type == 3 && m.Message.GetProperty("on").GetBoolean()),
            "reconnect and a new mirror honor disabled audio and clipboard settings");

        clipboardToggle.IsChecked = true;
        model.SetActiveClipboard(clip);
        await UntilAsync(() => phone.ClipboardMessages.Any(m => m.Type == 3 && m.Message.GetProperty("on").GetBoolean()), ct);
        await Task.Delay(50, ct);
        const string reenabledRemote = "reenabled phone clipboard synthetic text";
        await phone.PushClipboardAsync(reenabledRemote);
        await UntilAsync(async () => await clip.TryGetTextAsync() == reenabledRemote, ct);
        audioToggle.IsChecked = true;
        await UntilAsync(() => players.Count > 1 && players.Last().Writes.Count > 0, ct);
        Program.Check(true, "reenabling both switches resumes synchronization without reconnecting");
        audioToggle.IsChecked = false; audioToggle.IsChecked = true; audioToggle.IsChecked = false; audioToggle.IsChecked = true;
        clipboardToggle.IsChecked = false; clipboardToggle.IsChecked = true; clipboardToggle.IsChecked = false; clipboardToggle.IsChecked = true;
        await UntilAsync(() => phone.AudioSubscriptions.Last().GetProperty("on").GetBoolean() && phone.ClipboardMessages.Last(m => m.Type == 3).Message.GetProperty("on").GetBoolean() && !players.Last().Disposed, ct);
        Program.Check(true, "rapid setting changes serialize unsubscribe and resubscribe to the final enabled intent");
        model.SetActiveClipboard(clip);
        await clip.SetTextAsync("rapid toggle final clipboard synthetic text");
        await UntilAsync(() => phone.ClipboardMessages.Any(m => m.Type == 2 && m.Message.GetProperty("text").GetString() == "rapid toggle final clipboard synthetic text"), ct);
        Program.Check(true, "rapid clipboard off/on restores active-window polling");
        model.SelectedApp = model.Apps.First();
        await model.OpenSelectedAppCommand.Execute().ToTask();
        var application = model.Mirrors.Single(m => !m.IsPhone);
        model.SetActiveMirror(application);
        await UntilAsync(() => phone.AudioSubscriptions.Last().GetProperty("sessionId").GetString() == application.SessionId, ct);
        Program.Check(players.Count(p => !p.Disposed) == 1, "multiple mirrors share one output and focus only changes the audio session owner");
        await application.DisposeAsync();
        await UntilAsync(() => phone.AudioSubscriptions.Last().GetProperty("sessionId").GetString() == model.Mirrors.Single().SessionId, ct);
        await model.Mirrors.Single().DisposeAsync();
        await UntilAsync(() => !phone.AudioSubscriptions.Last().GetProperty("on").GetBoolean(), ct);
        Program.Check(players.All(p => p.Disposed), "closing the final mirror stops audio and releases playback");

        // Exercise the actual native PCM path without playing sound through the user's speakers.
        SDL.SetHint("SDL_AUDIO_DRIVER", "dummy");
        try
        {
            using var native = new SdlAudioPlayback();
            native.Configure(new(48000, 1, "pcm_s16le"));
            native.Write(MockPhone.SyntheticPcm); native.Write(MockPhone.SyntheticPcm);
            native.Configure(new(44100, 2, "pcm_s16le"));
            native.Write(new byte[44100 * 4]);
            native.Dispose(); native.Dispose();
            Program.Check(true, "SDL dummy output accepts mono/stereo PCM, configuration changes, bounded backlog and repeated disposal");
        }
        finally { SDL.ResetHint("SDL_AUDIO_DRIVER"); }
        model.SetActiveClipboard(null);
    }

    private static async Task UntilAsync(Func<bool> condition, CancellationToken ct,
        [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(condition))] string? description = null)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Sharing check: " + description);
            await Task.Delay(10, ct);
        }
    }
    private static async Task UntilAsync(Func<Task<bool>> condition, CancellationToken ct,
        [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(condition))] string? description = null)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (!await condition())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Sharing check: " + description);
            await Task.Delay(10, ct);
        }
    }
}
