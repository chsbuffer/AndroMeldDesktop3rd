using System.Runtime.Versioning;
using System.Text;
using Andromeld.Desktop.Services;
using Andromeld.Desktop.ViewModels;
using Andromeld.Desktop.Views;
using Andromeld.Platform.Windows;
using Andromeld.Protocol;
using Andromeld.Testing;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Iciclecreek.Terminal;
using TerminalWindow = Andromeld.Desktop.Views.TerminalWindow;
using ReactiveUI.Avalonia;
using ReactiveUI.Primitives;

namespace Andromeld.Desktop.Tests;

[SupportedOSPlatform("windows6.1")]
internal static class TerminalNativeChecks
{
    public static void Run()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        Exception? failure = null;
        var completed = false;
        AppBuilder.Configure<global::Andromeld.Desktop.App>().UseAndromeldPlatform()
            .UseSkia().UseHarfBuzz().WithInterFont().UseReactiveUI(_ => { }).SetupWithoutStarting();
        Dispatcher.UIThread.Post(async () =>
        {
            TerminalWindow? window = null;
            try
            {
                await using var phone = await MockPhone.StartAsync();
                await using var client = new AndromeldClient();
                await client.ConnectAsync(phone.Endpoint, "terminal-native-synthetic", "test-token", null, stop.Token);
                await using var model = new TerminalViewModel(client, stop.Token);
                window = new(model);
                window.Show(); window.UpdateLayout();
                await model.StartAsync();
                var control = window.Terminal;
                await UntilAsync(() => control.FindInBuffer("Android 终端 ✓") == 1, stop.Token);
                control.ClearSearch();
                Program.Check(WindowTaskbarIdentity.Get(window.TryGetPlatformHandle()!.Handle)?.StartsWith("AndroMeld.Desktop.Terminal.", StringComparison.Ordinal) == true,
                    "Terminal installs its own native taskbar identity");
                var view = control.GetVisualDescendants().OfType<TerminalView>().Single();
                window.Activate(); view.Focus();
                var handle = window.TryGetPlatformHandle()!.Handle;
                WindowsInputChecks.InjectKey(handle, 0x1B, KeyModifiers.None, '\u001b');
                WindowsInputChecks.InjectKey(handle, 0x09, KeyModifiers.None, '\t');
                WindowsInputChecks.InjectKey(handle, 0x43, KeyModifiers.Control, '\u0003');
                WindowsInputChecks.InjectKey(handle, 0x44, KeyModifiers.Control, '\u0004');
                await UntilAsync(() => phone.TerminalInputs.Count >= 4, stop.Token);
                var bytes = phone.TerminalInputs.SelectMany(x => x.Bytes).ToArray();
                Program.Check(bytes.SequenceEqual(new byte[] { 27, 9, 3, 4 }) && window.IsVisible, $"Win32 terminal Esc, Tab, interrupt and live Ctrl+D reach the remote PTY ({Convert.ToHexString(bytes)})");
                await phone.PushTerminalOutputAsync(1, Encoding.UTF8.GetBytes("\r\n\u001b[36mNative Android 终端 ✓\u001b[0m\r\n$ "));
                await UntilAsync(() => control.FindInBuffer("Native Android") == 1, stop.Token);
                control.ClearSearch();
                await phone.PushTerminalOutputAsync(1, Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("\r\n终端输出 ✓", 15000))));
                window.Width = 640; window.Height = 420; window.UpdateLayout();
                await phone.PushTerminalOutputAsync(1, Encoding.UTF8.GetBytes("\u001b[2J\u001b[H\u001b[36mNative Android 终端 ✓\u001b[0m\r\n$ "));
                await UntilAsync(() => control.FindInBuffer("Native Android") == 1, stop.Token);
                control.ClearSearch();
                view.ViewportY = view.MaxScrollback;
                Program.Check(control.Process == "" && model.IsRunning, "native terminal parses remote ANSI and Chinese without starting a local process");
                await phone.ExitTerminalAsync(1, 3);
                await UntilAsync(() => model.CanRestart && !model.IsRunning, stop.Token);
                WindowsInputChecks.InjectKey(handle, 0x0D, KeyModifiers.None, '\r');
                await UntilAsync(() => model.IsRunning, stop.Token);
                WindowsInputChecks.InjectKey(handle, 0x41, KeyModifiers.None, 'a');
                await UntilAsync(() => phone.TerminalInputs.Any(input => input.Bytes.Contains((byte)'a')), stop.Token);
                Program.Check(phone.TerminalCommands.Count(command => command.GetProperty("c").GetString() == "term-open") == 2 &&
                    phone.TerminalInputs.SelectMany(input => input.Bytes).SequenceEqual(new byte[] { 27, 9, 3, 4, (byte)'a' }),
                    "Enter restarts an exited terminal once without leaking its character into the new PTY");
                await client.DisposeAsync();
                await UntilAsync(() => !model.IsConnected && !model.IsRunning, stop.Token);
                Program.Check(window.IsVisible && !model.CanRestart && control.FindInBuffer("Android 终端") >= 1,
                    "native disconnect preserves the output buffer and disables restart on the old connection");
                WindowsInputChecks.InjectKey(handle, 0x44, KeyModifiers.Control, '\u0004');
                await UntilAsync(() => !window.IsVisible && model.IsDisposed, stop.Token);
                Program.Check(true, "Ctrl+D closes the ended terminal window and disposes its model");
            }
            catch (Exception ex) { failure = ex; }
            finally { window?.Close(); completed = true; stop.Cancel(); }
        });
        Dispatcher.UIThread.MainLoop(stop.Token);
        if (!completed) throw new TimeoutException("Native terminal checks exceeded watchdog");
        if (failure is not null) throw new Exception("Native terminal checks failed", failure);
    }

    private static async Task UntilAsync(Func<bool> condition, CancellationToken ct)
    {
        while (!condition()) await Task.Delay(20, ct);
    }
}
