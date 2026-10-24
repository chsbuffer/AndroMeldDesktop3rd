using Andromeld.Platform.Windows;

namespace Andromeld.Desktop.Services;

public interface IDesktopCameraSource
{
    // Called serially on the window's owning UI thread. Start may await system authorization;
    // failed/canceled starts release their partial resources before returning.
    Task StartAsync(CancellationToken ct);
    Task StopAsync();
    // Also observed while the window is minimized and no frame is being requested.
    Task WaitForCloseAsync(CancellationToken ct) => Task.Delay(Timeout.Infinite, ct);

    // The implementation owns its worker and returns one complete JPEG.
    Task<byte[]> CaptureAsync(DesktopCaptureRegion region, CancellationToken ct);
}
