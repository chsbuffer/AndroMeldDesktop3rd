using System.Net.WebSockets;

using Andromeld.Protocol;

using Avalonia.Input.Platform;
using Avalonia.Threading;

namespace Andromeld.Desktop.Services;

/// <summary>
///     UI-thread owned. Avalonia handles the platform clipboard; a single async operation serializes I/O.
///     Only an active application window supplies a clipboard, as required by Wayland focus rules.
/// </summary>
public sealed class ClipboardSharing : IAsyncDisposable
{
    private readonly AndromeldClient _client;
    private readonly Action<Exception> _onError;
    private readonly CancellationTokenSource _stop;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private IClipboard? _clipboard;
    private bool _disposed;
    private Task _operation = Task.CompletedTask;
    private string? _pending, _lastText;
    private long _revision;

    public ClipboardSharing(AndromeldClient client, Action<Exception> onError, CancellationToken connection)
    {
        _client = client;
        _onError = onError;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(connection);
        _client.ClipboardReceived += Receive;
        _timer.Tick += Tick;
    }

    public async ValueTask DisposeAsync()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _timer.Tick -= Tick;
        _client.ClipboardReceived -= Receive;
        await _stop.CancelAsync();
        await _operation;
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
        try { await _client.SetClipboardEnabledAsync(false, timeout.Token); }
        catch (Exception ex) when (ex is IOException or WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            /* Best-effort disable while shutting down. */
        }

        _clipboard = null;
        _pending = _lastText = null;
        _stop.Dispose();
    }

    public Task StartAsync()
    {
        return _client.SetClipboardEnabledAsync(true, _stop.Token);
    }

    public void SetActiveClipboard(IClipboard? clipboard)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_disposed)
        {
            return;
        }

        _clipboard = clipboard;
        if (clipboard is null)
        {
            _timer.Stop();
        }
        else
        {
            _timer.Start();
            Synchronize();
        }
    }

    private void Receive(string text)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed || string.Equals(text, _lastText, StringComparison.Ordinal) || string.Equals(text, _pending, StringComparison.Ordinal))
            {
                return;
            }

            _pending = text;
            _revision++;
            Synchronize();
        });
    }

    private void Tick(object? sender, EventArgs e)
    {
        Synchronize();
    }

    private void Synchronize()
    {
        if (!_disposed && _clipboard is not null && _operation.IsCompleted)
        {
            _operation = SynchronizeAsync(_clipboard);
        }
    }

    private async Task SynchronizeAsync(IClipboard clipboard)
    {
        try
        {
            long revision = _revision;
            if (_pending is { } incoming)
            {
                await clipboard.SetTextAsync(incoming).WaitAsync(_stop.Token);
                if (_disposed || _clipboard != clipboard || revision != _revision)
                {
                    return;
                }

                _lastText = incoming;
                _pending = null;
            }

            revision = _revision;
            string? text = await clipboard.TryGetTextAsync().WaitAsync(_stop.Token);
            if (_disposed || _clipboard != clipboard || revision != _revision || string.IsNullOrEmpty(text) || string.Equals(text, _lastText, StringComparison.Ordinal))
            {
                return;
            }

            _lastText = text;
            await _client.SetClipboardTextAsync(text, _stop.Token);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            /* Shutdown owns cancellation. */
        }
        catch (Exception ex)
        {
            if (!_disposed)
            {
                _onError(ex);
            }
        }
    }
}