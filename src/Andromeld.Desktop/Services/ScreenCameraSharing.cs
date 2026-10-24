using Andromeld.Platform.Windows;

using Avalonia.Threading;

namespace Andromeld.Desktop.Services;

/// <summary>UI-owned lifecycle, geometry and one in-flight frame. Capture/encoding live on the source's worker.</summary>
public sealed class ScreenCameraSharing : IAsyncDisposable
{
    private readonly Action<Exception> _error;
    private readonly Func<DesktopCaptureRegion?> _region;
    private readonly Action<bool> _running;
    private readonly Func<byte[], CancellationToken, Task> _send;
    private readonly IDesktopCameraSource _source;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(1000d / 15) };
    private CancellationTokenSource? _capture;
    private bool _desired, _started, _disposed;
    private Task? _frame, _transition;

    public ScreenCameraSharing(IDesktopCameraSource source, Func<DesktopCaptureRegion?> region,
        Func<byte[], CancellationToken, Task> send, Action<bool> running, Action<Exception> error)
    {
        _source = source;
        _region = region;
        _send = send;
        _running = running;
        _error = error;
        _timer.Tick += Tick;
    }

    public async ValueTask DisposeAsync()
    {
        Dispatcher.UIThread.VerifyAccess();
        _disposed = true;
        await SetActiveAsync(false).ConfigureAwait(true);
        _timer.Tick -= Tick;
    }

    public Task SetActiveAsync(bool active)
    {
        Dispatcher.UIThread.VerifyAccess();
        _desired = !_disposed && active;
        if (!_desired)
        {
            _capture?.Cancel();
        }

        if (_transition is { IsCompleted: false })
        {
            return _transition;
        }

        _transition = ReconcileAsync();
        return _transition;
    }

    private async Task ReconcileAsync()
    {
        do
        {
            if (_desired && !_started)
            {
                _capture = new CancellationTokenSource();
                try
                {
                    await _source.StartAsync(_capture.Token).ConfigureAwait(true);
                    _started = true;
                    if (_desired && !_capture.IsCancellationRequested)
                    {
                        _running(true);
                        _timer.Start();
                        _ = WatchClosedAsync(_capture.Token);
                    }
                }
                catch (Exception ex)
                {
                    if (!_capture.IsCancellationRequested)
                    {
                        _desired = false;
                        _error(ex);
                    }
                }

                if (!_started)
                {
                    _capture.Dispose();
                    _capture = null;
                }
            }

            if (_started && (!_desired || _capture is { IsCancellationRequested: true }))
            {
                _timer.Stop();
                if (_capture is { } capture)
                {
                    await capture.CancelAsync().ConfigureAwait(true);
                }

                if (_frame is not null)
                {
                    await _frame.ConfigureAwait(true);
                }

                try { await _source.StopAsync().ConfigureAwait(true); }
                catch (Exception ex) { _error(ex); }

                _started = false;
                _running(false);
                _capture?.Dispose();
                _capture = null;
                _frame = null;
            }
        } while (_desired != _started);
    }

    private void Tick(object? sender, EventArgs e)
    {
        if (!_desired || _capture is null || _frame is { IsCompleted: false } || _region() is not { } region)
        {
            return;
        }

        _frame = ProduceFrameAsync(region, _capture.Token);
    }

    private async Task WatchClosedAsync(CancellationToken ct)
    {
        try { await _source.WaitForCloseAsync(ct).ConfigureAwait(true); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        Dispatcher.UIThread.Post(() =>
        {
            if (ct.IsCancellationRequested) return;
            _error(new IOException("系统已停止屏幕共享。"));
            _ = SetActiveAsync(false);
        });
    }

    private async Task ProduceFrameAsync(DesktopCaptureRegion region, CancellationToken ct)
    {
        try
        {
            byte[] jpeg = await _source.CaptureAsync(region, ct).ConfigureAwait(true);
            ct.ThrowIfCancellationRequested();
            await _send(jpeg, ct).ConfigureAwait(true);
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            /* Stop never publishes the completed frame. */
        }
        catch (Exception ex)
        {
            _desired = false;
            _timer.Stop();
            _error(ex);
            // Reconcile after this task returns, so shutdown cannot await itself.
            Dispatcher.UIThread.Post(() => _ = SetActiveAsync(false));
        }
    }
}
