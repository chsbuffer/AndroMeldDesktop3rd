using System.Runtime.Versioning;

using Andromeld.Platform.Windows;

namespace Andromeld.Desktop.Services;

[SupportedOSPlatform("windows10.0.19041")]
public sealed class WindowsDesktopCameraSource(nint window) : IDesktopCameraSource
{
    public Task StartAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        DesktopRegionCapture.ExcludeWindow(window, true);
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        DesktopRegionCapture.ExcludeWindow(window, false);
        return Task.CompletedTask;
    }

    public Task<byte[]> CaptureAsync(DesktopCaptureRegion region, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            DesktopCaptureFrame frame = DesktopRegionCapture.Capture(region);
            ct.ThrowIfCancellationRequested();
            return ScreenCameraImage.Encode(frame);
        }, ct);
    }
}
