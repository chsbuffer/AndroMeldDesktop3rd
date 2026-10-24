using Andromeld.Desktop.Services;
using Andromeld.Desktop.ViewModels;
using Andromeld.Protocol;

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

using Iciclecreek.Terminal;

using ReactiveUI.Primitives;

namespace Andromeld.Desktop.Views;

public partial class TerminalWindow : Window
{
    private readonly TerminalViewModel _model;
    private RemoteTerminalConnection? _connection;
    private bool _restartKeyDown;

    public TerminalWindow(TerminalViewModel model)
    {
        _model = model;
        InitializeComponent();
        DataContext = model;
        DesktopPlatform.ConfigureWindow(this, "Terminal");
        model.SessionOpened += AttachSession;
        Console.ProcessExited += (_, _) => model.OutputDrained();
        Console.SizeChanged += (_, _) => UpdateSize();
        AddHandler(KeyDownEvent, TerminalKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, TerminalKeyUp, RoutingStrategies.Tunnel);
        Opened += (_, _) =>
        {
            UpdateSize();
            FocusTerminal();
        };
    }

    public TerminalControl Terminal => Console;

    private bool IsLegacyEscape(KeyEventArgs e)
    {
        return e.Key == Key.Escape && Console.IsKeyboardFocusWithin &&
               Console.Terminal is { KittyKeyboardActive: false, Win32InputMode: false };
    }

    private async void TerminalKeyDown(object? sender, KeyEventArgs e)
    {
        if (_model.HasExited && e.Key == Key.D && e.KeyModifiers == KeyModifiers.Control)
        {
            e.Handled = true;
            Close();
            return;
        }

        if (e.Key == Key.Enter && (_restartKeyDown || (_model.HasExited && _model.CanRestart && e.KeyModifiers == KeyModifiers.None)))
        {
            e.Handled = true;
            if (_restartKeyDown)
            {
                return;
            }

            _restartKeyDown = true;
            try { await _model.RestartCommand.Execute().ToTask().ConfigureAwait(true); }
            catch (Exception ex) { _model.Report(ex); }

            return;
        }

        // The control assumes a local ConPTY on Windows and encodes plain Esc as a Win32
        // INPUT_RECORD even without mode 9001. Our peer is an Android PTY: use legacy ESC.
        // Negotiated Kitty/Win32 protocols still belong to the emulator's normal key path.
        if (!IsLegacyEscape(e) || !_model.IsRunning)
        {
            return;
        }

        e.Handled = true;
        try { await Console.SendInputAsync((e.KeyModifiers & KeyModifiers.Alt) != KeyModifiers.None ? "\u001b\u001b" : "\u001b").ConfigureAwait(true); }
        catch (Exception ex) { _model.Report(ex); }
    }

    private void TerminalKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _restartKeyDown)
        {
            _restartKeyDown = false;
            e.Handled = true;
        }

        if (IsLegacyEscape(e) && _model.IsRunning)
        {
            e.Handled = true;
        }
    }

    private void FocusTerminal()
    {
        Console.GetVisualDescendants().OfType<TerminalView>().FirstOrDefault()?.Focus();
    }

    private void UpdateSize()
    {
        if (Console.Terminal is not { } terminal)
        {
            return;
        }

        _model.Columns = Math.Clamp(terminal.Cols, 1, 500);
        _model.Rows = Math.Clamp(terminal.Rows, 1, 300);
    }

    private void AttachSession(TerminalSession session)
    {
        Console.DetachConnection();
        _connection?.Dispose();
        Console.Terminal?.Reset();
        _connection = new RemoteTerminalConnection(session, _model.Report);
        Console.AttachConnection(_connection);
        UpdateSize();
        _connection.Resize(_model.Columns, _model.Rows);
        FocusTerminal();
    }

    private async void CopyClick(object? sender, RoutedEventArgs e)
    {
        try { await Console.CopyAsync().ConfigureAwait(true); }
        catch (Exception ex) { _model.Report(ex); }
    }

    private async void PasteClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            await Console.PasteAsync().ConfigureAwait(true);
            FocusTerminal();
        }
        catch (Exception ex) { _model.Report(ex); }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (!e.Cancel)
        {
            DesktopPlatform.ClearWindowIdentity(this);
        }
    }

    protected override async void OnClosed(EventArgs e)
    {
        _model.SessionOpened -= AttachSession;
        Console.DetachConnection();
        _connection?.Dispose();
        Console.Dispose();
        base.OnClosed(e);
        await _model.DisposeAsync().ConfigureAwait(true);
    }
}