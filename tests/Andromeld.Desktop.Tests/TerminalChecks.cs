using System.Text;
using Andromeld.Desktop.ViewModels;
using Andromeld.Desktop.Views;
using Andromeld.Testing;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.VisualTree;
using Iciclecreek.Terminal;
using ReactiveUI.Primitives;

namespace Andromeld.Desktop.Tests;

internal static class TerminalChecks
{
    public static async Task RunWindowAsync(MainViewModel model, MainWindow main, MockPhone phone, CancellationToken ct)
    {
        await model.OpenTerminalCommand.Execute().ToTask();
        var window = main.TerminalWindows.Single();
        var terminal = model.Terminals.Single();
        var control = window.Terminal;
        await UntilAsync(() => control.FindInBuffer("Android 终端 ✓") == 1, ct);
        Program.Check(terminal.IsRunning && control.Process == "", "platform terminal window displays remote UTF-8/ANSI without a local PTY");
        await control.SendInputAsync("echo synthetic\n");
        await UntilAsync(() => phone.TerminalInputs.Any(x => Encoding.UTF8.GetString(x.Bytes) == "echo synthetic\n"), ct);
        window.Width = 640; window.Height = 420;
        await UntilAsync(() =>
        {
            window.UpdateLayout();
            return control.Bounds.Width < 640 && control.Bounds.Height < 420 && phone.TerminalCommands.Any(x =>
                x.GetProperty("c").GetString() == "term-resize" &&
                x.GetProperty("cols").GetInt32() == control.Terminal.Cols && x.GetProperty("rows").GetInt32() == control.Terminal.Rows);
        }, ct);
        Program.Check(true, "platform terminal sends remote input and resizes the Android PTY");
        window.Close();
        await UntilAsync(() => model.Terminals.Count == 0, ct);
        Program.Check(model.IsConnected && main.TerminalWindows.Count == 0 && model.CanUseFiles,
            "closing platform terminal preserves the device connection and file operations");
    }

    public static async Task RunAsync(MainViewModel model, MainWindow main, MockPhone phone, CancellationToken ct)
    {
        var openedBefore = phone.TerminalCommands.Count(x => x.GetProperty("c").GetString() == "term-open");
        await model.OpenTerminalCommand.Execute().ToTask();
        var window = main.TerminalWindows.Single();
        var terminal = model.Terminals.Single();
        var control = window.Terminal;
        var view = control.GetVisualDescendants().OfType<TerminalView>().Single();
        await UntilAsync(() => control.FindInBuffer("Android 终端 ✓") == 1, ct);
        control.ClearSearch();
        await UntilAsync(() => phone.TerminalCommands.Any(x => x.GetProperty("c").GetString() == "term-resize"), ct);
        var id = phone.TerminalCommands.Last(x => x.GetProperty("c").GetString() == "term-resize").GetProperty("id").GetInt32();
        Program.Check(control.Process == "" && terminal.IsRunning && control.BufferSize == 5000 && model.Mirrors.Count == 0,
            "independent terminal parses split Chinese UTF-8 and ANSI without launching a local shell or video session");
        window.Activate(); view.Focus();
        window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
        window.KeyRelease(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
        window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
        window.KeyRelease(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
        window.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, "c");
        window.KeyRelease(Key.C, RawInputModifiers.Control, PhysicalKey.C, "c");
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        await UntilAsync(() => phone.TerminalInputs.Count >= 4, ct);
        var input = phone.TerminalInputs.SelectMany(x => x.Bytes).ToArray();
        Program.Check(input.SequenceEqual(new byte[] { 9, 27, 91, 65, 3, 27 }),
            $"terminal owns Tab, arrow, Ctrl+C and Esc rather than triggering Android navigation ({Convert.ToHexString(input)})");
        await phone.PushTerminalOutputAsync(id, Encoding.UTF8.GetBytes("\u001b[?1049h\u001b[H交互界面"));
        await UntilAsync(() => control.FindInBuffer("交互界面") == 1, ct);
        await phone.PushTerminalOutputAsync(id, Encoding.UTF8.GetBytes("\u001b[?1049l"));
        await UntilAsync(() => control.FindInBuffer("Android 终端 ✓") == 1, ct);
        control.ClearSearch();
        control.Terminal.Selection.SelectAll();
        var beforeCopy = phone.TerminalInputs.Count;
        window.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, "c");
        window.KeyRelease(Key.C, RawInputModifiers.Control, PhysicalKey.C, "c");
        await UntilAsync(() => phone.TerminalInputs.Count == beforeCopy && control.Terminal.Selection.HasSelection, ct);
        Program.Check((await window.Clipboard!.TryGetTextAsync())?.Contains("Android 终端", StringComparison.Ordinal) == true && phone.TerminalInputs.Count == beforeCopy,
            "alternate-screen programs restore the shell buffer and Ctrl+C copies a selection without interrupting");
        control.Terminal.Selection.ClearSelection();
        await phone.PushTerminalOutputAsync(id, Encoding.UTF8.GetBytes("\u001b[?2004h"));
        await UntilAsync(() => control.Terminal.BracketedPasteMode, ct);
        model.SetActiveClipboard(null);
        await window.Clipboard!.SetTextAsync("echo 合成粘贴\n");
        await control.PasteAsync();
        await UntilAsync(() => phone.TerminalInputs.Any(x => Encoding.UTF8.GetString(x.Bytes).Contains("\u001b[200~echo 合成粘贴\n\u001b[201~", StringComparison.Ordinal)), ct);
        Program.Check(true, "paste uses Avalonia clipboard and the remote shell's bracketed-paste mode");
        var output = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("\r\n终端输出 ✓", 15000)));
        await phone.PushTerminalOutputAsync(id, output);
        await UntilAsync(() => phone.TerminalCommands.Any(x => x.GetProperty("c").GetString() == "term-ack" && x.GetProperty("received").GetInt64() >= 200000), ct);
        Program.Check(model.IsConnected, "terminal emulator consumes more than the server's 128 KiB window and sends cumulative ACKs");
        window.Width = 640; window.Height = 420; window.UpdateLayout();
        await UntilAsync(() =>
        {
            window.UpdateLayout();
            return view.Bounds.Width < 640 && view.Bounds.Height < 420 && phone.TerminalCommands.Any(x => x.GetProperty("c").GetString() == "term-resize" && x.GetProperty("cols").GetInt32() == control.Terminal.Cols && x.GetProperty("rows").GetInt32() == control.Terminal.Rows);
        }, ct);
        Program.Check(true, "terminal window resizes the Android PTY in character columns and rows");
        await phone.PushTerminalOutputAsync(id, Encoding.UTF8.GetBytes("\u001b[2J\u001b[H\u001b[36mAndroid 终端 ✓\u001b[0m\r\n$ echo 合成输出\r\n合成输出\r\n$ "));
        await UntilAsync(() => control.FindInBuffer("合成输出") >= 2, ct);
        control.ClearSearch();
        view.ViewportY = view.MaxScrollback;
        control.Refresh();
        await phone.ExitTerminalAsync(id, 7);
        await UntilAsync(() => !terminal.IsRunning && terminal.CanRestart, ct);
        Program.Check(terminal.Status.Contains('7') && window.IsVisible && control.FindInBuffer("合成输出") >= 2,
            "remote exit drains output, keeps the terminal buffer visible and enables restart");
        await terminal.RestartCommand.Execute().ToTask();
        await UntilAsync(() => terminal.IsRunning && control.FindInBuffer("Android 终端 ✓") == 1, ct);
        Program.Check(phone.TerminalCommands.Count(x => x.GetProperty("c").GetString() == "term-open") == openedBefore + 2,
            "restart opens a fresh PTY and resets the previous terminal buffer");
        window.Close();
        await UntilAsync(() => model.Terminals.Count == 0 && phone.TerminalCommands.Any(x => x.GetProperty("c").GetString() == "term-close"), ct);
        Program.Check(model.IsConnected && main.TerminalWindows.Count == 0 && model.CanUseFiles,
            "closing Terminal releases its PTY while retaining device and file connectivity");
    }

    private static async Task UntilAsync(Func<bool> condition, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }

    public static async Task DisconnectDeviceAsync(MainViewModel model, MainWindow main, CancellationToken ct)
    {
        await model.OpenTerminalCommand.Execute().ToTask();
        var window = main.TerminalWindows.Single();
        var terminal = model.Terminals.Single();
        await UntilAsync(() => window.Terminal.FindInBuffer("Android 终端") == 1, ct);
        await model.DisconnectCommand.Execute().ToTask();
        await Task.Delay(25, ct);
        Program.Check(window.IsVisible && !terminal.IsConnected && !terminal.CanRestart && !model.CanOpenTerminal && terminal.Status == "设备已断开连接" &&
            window.Terminal.FindInBuffer("Android 终端") == 1,
            "Disconnect Device ends open PTYs and preserves their independent buffers without reusing the old connection");
        window.Close();
        await UntilAsync(() => model.Terminals.Count == 0, ct);
    }
}
