using Andromeld.Protocol;

using Avalonia.Threading;

using ReactiveUI.Primitives;
using ReactiveUI.SourceGenerators;

namespace Andromeld.Desktop.ViewModels;

/// <summary>UI owns properties, commands, session attachment and disposal.</summary>
public sealed partial class TerminalViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly IObservable<bool> _canRestart;
    private readonly AndromeldClient _client;
    private readonly IDisposable _errors;
    private readonly CancellationTokenSource _stop;
    private TerminalSession? _session;

    public TerminalViewModel(AndromeldClient client, CancellationToken connection)
    {
        _client = client;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(connection);
        Title = $"终端 · {client.DeviceName}";
        _canRestart = this.WhenAnyValue(x => x.CanRestart);
        _errors = RestartCommand.ThrownExceptions.Subscribe(Report);
    }

    public string Title { get; }
    public int Columns { get; set; } = 80;
    public int Rows { get; set; } = 24;
    public bool IsDisposed { get; private set; }
    [Reactive]
    public partial string Status { get; private set; } = "正在打开终端…";
    [Reactive(nameof(CanRestart), nameof(HasExited), nameof(ExitHint))]
    public partial bool IsRunning { get; private set; }
    [Reactive(nameof(CanRestart), nameof(HasExited), nameof(ExitHint))]
    public partial bool IsOpening { get; private set; }
    [Reactive(nameof(CanRestart), nameof(ExitHint))]
    public partial bool IsConnected { get; private set; } = true;
    public bool CanRestart => !IsRunning && !IsOpening && IsConnected;
    public bool HasExited => _session is not null && !IsRunning && !IsOpening;
    public string ExitHint => CanRestart ? "可以使用 Ctrl+D 关闭此终端，或按 Enter 重新启动。" : "可以使用 Ctrl+D 关闭此终端。";

    public async ValueTask DisposeAsync()
    {
        if (IsDisposed)
        {
            return;
        }

        IsDisposed = true;
        await _stop.CancelAsync().ConfigureAwait(true);
        if (_session is not null)
        {
            await _session.DisposeAsync().ConfigureAwait(true);
        }

        Closed?.Invoke();
        _errors.Dispose();
        RestartCommand.Dispose();
        _stop.Dispose();
    }

    public event Action<TerminalSession>? SessionOpened;
    public event Action? Closed;

    [ReactiveCommand(CanExecute = nameof(_canRestart))]
    private Task RestartAsync()
    {
        return StartAsync();
    }

    public async Task StartAsync()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (IsDisposed || IsOpening || IsRunning || !IsConnected)
        {
            return;
        }

        IsOpening = true;
        Status = "正在打开终端…";
        try
        {
            TerminalSession session = await _client.OpenTerminalAsync(Math.Clamp(Columns, 1, 500), Math.Clamp(Rows, 1, 300), _stop.Token)
                .ConfigureAwait(true);
            if (IsDisposed)
            {
                await session.DisposeAsync().ConfigureAwait(true);
                return;
            }

            _session = session;
            IsRunning = true;
            Status = "已连接";
            SessionOpened?.Invoke(session);
            _ = ObserveExitAsync(session);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            /* Window/device is closing. */
        }
        catch (Exception ex)
        {
            if (_session is not null)
            {
                await _session.DisposeAsync().ConfigureAwait(true);
            }

            IsRunning = false;
            Report(ex);
        }
        finally { IsOpening = false; }
    }

    private async Task ObserveExitAsync(TerminalSession session)
    {
        TerminalExit exit = await session.Completion.ConfigureAwait(true);
        if (IsDisposed || !IsConnected || !ReferenceEquals(_session, session))
        {
            return;
        }

        // Keep the control attached to drain the final output. Restart becomes available when
        // its parser reaches EOF, via OutputDrained, rather than on the earlier term-exit message.
        Status = exit.Code is >= 0 ? $"终端已退出（{exit.Code}）" : "终端已结束";
        if (exit.Error is not null)
        {
            Status = exit.Error.Message;
        }

        if (exit.Error is not null)
        {
            IsConnected = false;
        }
    }

    public void OutputDrained()
    {
        if (!IsDisposed)
        {
            IsRunning = false;
        }
    }

    public void Report(Exception error)
    {
        if (!IsDisposed && error is not OperationCanceledException)
        {
            Status = error.Message;
        }
    }

    public async Task DisconnectAsync()
    {
        if (IsDisposed)
        {
            return;
        }

        IsConnected = false;
        Status = "设备已断开连接";
        await _stop.CancelAsync().ConfigureAwait(true);
        if (_session is not null)
        {
            await _session.DisposeAsync().ConfigureAwait(true);
        }

        IsRunning = false;
    }
}