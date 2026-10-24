using Andromeld.Platform.Linux;
using Andromeld.Platform.Windows;

namespace Andromeld.Desktop.Services;

/// <summary>UI owns the session and serializes start/stop. Native capture and JPEG encoding run
/// on one in-flight worker, which has finished before the UI releases its resources.</summary>
public sealed class WaylandDesktopCameraSource : IDesktopCameraSource
{
    private ScreenCastPortal? _portal;
    private PipeWireFrameCapture? _capture;
    public Task WaitForCloseAsync(CancellationToken ct)
        => (_portal ?? throw new InvalidOperationException("屏幕共享尚未连接。")).Closed.WaitAsync(ct);

    public async Task StartAsync(CancellationToken ct)
    {
        var portal = await ScreenCastPortal.OpenAsync("", ct).ConfigureAwait(true);
        PipeWireFrameCapture? capture = null;
        try
        {
            capture = await Task.Run(() => new PipeWireFrameCapture(portal.Remote, portal.NodeId, portal.Serial), ct).ConfigureAwait(true);
            ct.ThrowIfCancellationRequested();
            _capture = capture;
            _portal = portal;
        }
        catch
        {
            if (capture is not null) await Task.Run(capture.Dispose, CancellationToken.None).ConfigureAwait(true);
            await portal.DisposeAsync().ConfigureAwait(true);
            throw;
        }
    }

    public async Task StopAsync()
    {
        var capture = _capture;
        var portal = _portal;
        _capture = null;
        _portal = null;
        try
        {
            if (capture is not null) await Task.Run(capture.Dispose).ConfigureAwait(true);
        }
        finally
        {
            if (portal is not null) await portal.DisposeAsync().ConfigureAwait(true);
        }
    }

    public Task<byte[]> CaptureAsync(DesktopCaptureRegion region, CancellationToken ct)
    {
        var capture = _capture ?? throw new InvalidOperationException("屏幕共享尚未启动。");
        var closed = _portal?.Closed ?? throw new InvalidOperationException("屏幕共享尚未连接。");
        return Task.Run(() =>
        {
            var pixels = capture.Capture(ct, closed);
            ct.ThrowIfCancellationRequested();
            return ScreenCameraImage.Encode(PipeWireFrameCapture.Width, PipeWireFrameCapture.Height, pixels);
        }, ct);
    }
}
