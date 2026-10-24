using Andromeld.Desktop.Services;
using Andromeld.Platform.Windows;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Andromeld.Desktop.Views;

public partial class MirrorWindow
{
    private readonly IDesktopCameraSource? _cameraSource;
    private ScreenCameraSharing? _camera;
    private bool _cameraClosing, _cameraClosed;
    private bool _cameraUsesDesktopRegion = true;

    private void InitializeScreenCamera()
    {
        if (_camera is not null || _model.IsPhone)
        {
            return;
        }

        IDesktopCameraSource? source = _cameraSource ?? DesktopPlatform.CreateScreenCameraSource(this);
        if (source is null) return;
        _cameraUsesDesktopRegion = source is not WaylandDesktopCameraSource;

        _camera = new ScreenCameraSharing(source, CameraRegion, _model.SendCameraFrameAsync,
            _model.SetScreenCameraRunning, _model.ReportTransfer);
        RefreshScreenCamera();
    }

    private DesktopCaptureRegion? CameraRegion()
    {
        if (!IsVisible || WindowState == WindowState.Minimized || Viewport.Bounds.Width <= 0 || Viewport.Bounds.Height <= 0)
        {
            return null;
        }

        // Standard Wayland sources are selected by the user; no global window position is available.
        if (!_cameraUsesDesktopRegion)
            return new DesktopCaptureRegion(0, 0, (int)Math.Ceiling(Viewport.Bounds.Width * RenderScaling), (int)Math.Ceiling(Viewport.Bounds.Height * RenderScaling));

        PixelPoint origin = Viewport.PointToScreen(default);
        PixelPoint end = Viewport.PointToScreen(new Point(Viewport.Bounds.Width, Viewport.Bounds.Height));
        return new DesktopCaptureRegion(origin.X, origin.Y, end.X - origin.X, end.Y - origin.Y);
    }

    private void RefreshScreenCamera()
    {
        if (_camera is not null)
        {
            _ = _camera.SetActiveAsync(!_cameraClosing && _model.CanControl && _model.ScreenCameraEnabled && _model.CameraRequested);
        }
    }

    private async Task CloseAfterCameraAsync(ValueTask cleanup)
    {
        await cleanup.ConfigureAwait(true);
        _cameraClosed = true;
        Dispatcher.UIThread.Post(Close);
    }
}
