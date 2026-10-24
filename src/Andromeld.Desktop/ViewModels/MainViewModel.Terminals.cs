using System.Collections.ObjectModel;

using ReactiveUI.SourceGenerators;

namespace Andromeld.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private readonly IObservable<bool> _canOpenTerminal;
    private bool _supportsTerminal;
    public bool CanOpenTerminal => IsConnected && _supportsTerminal;
    public ObservableCollection<TerminalViewModel> Terminals { get; } = [];
    public event Func<TerminalViewModel, Task>? TerminalRequested;

    [ReactiveCommand(CanExecute = nameof(_canOpenTerminal))]
    private async Task OpenTerminalAsync()
    {
        if (!CanOpenTerminal || _client is null)
        {
            return;
        }

        TerminalViewModel terminal = new(_client, _connection.Token);
        terminal.Closed += () => Terminals.Remove(terminal);
        Terminals.Add(terminal);
        if (TerminalRequested is { } show)
        {
            await show(terminal).ConfigureAwait(true);
        }

        await terminal.StartAsync().ConfigureAwait(true);
    }

    private async Task DisconnectTerminalsAsync()
    {
        // Leave exited/disconnected buffers available for selection and copy in their own windows.
        foreach (TerminalViewModel terminal in Terminals.ToArray())
        {
            await terminal.DisconnectAsync().ConfigureAwait(true);
        }
    }
}