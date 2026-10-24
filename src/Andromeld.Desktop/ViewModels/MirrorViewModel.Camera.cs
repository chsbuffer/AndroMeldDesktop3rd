using System.ComponentModel;

using Andromeld.Desktop.Services;

using ReactiveUI.SourceGenerators;

namespace Andromeld.Desktop.ViewModels;

public sealed partial class MirrorViewModel
{
    private void SaveScreenCameraPreference(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not nameof(ScreenCameraEnabled) || IsDisposed || App is not { } app ||
            _displayPreferences.GetScreenCamera(_displayDeviceKey, app) == ScreenCameraEnabled)
        {
            return;
        }

        try { _displayPreferences.SetScreenCamera(_displayDeviceKey, app, ScreenCameraEnabled); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = $"屏幕摄像头设置已生效，但未能保存：{ex.Message}";
        }
    }

    public bool SupportsScreenCamera => !IsPhone && DesktopPlatform.SupportsScreenCamera;
    public bool CanUseScreenCamera => SupportsScreenCamera && CanControl;
    [Reactive(nameof(ScreenCameraStatus))]
    public partial bool ScreenCameraEnabled { get; set; }
    [Reactive(nameof(ScreenCameraStatus))]
    public partial bool CameraRequested { get; private set; }
    [Reactive(nameof(ScreenCameraStatus))]
    public partial bool ScreenCameraRunning { get; private set; }
    public string ScreenCameraStatus
    {
        get
        {
            if (!SupportsScreenCamera)
            {
                return "屏幕摄像头需要 Windows 10 2004+ 或 Wayland 桌面";
            }

            if (ScreenCameraRunning)
            {
                return DesktopPlatform.IsWayland
                    ? "正在共享所选屏幕或窗口；可在系统共享提示中停止"
                    : "正在共享镜像窗口背后的桌面；截图可使用菜单中的“截图”，系统截图需先关闭屏幕摄像头";
            }

            if (CameraRequested && !ScreenCameraEnabled)
            {
                return "应用请求相机；可在显示菜单启用屏幕摄像头";
            }

            if (DesktopPlatform.IsWayland)
            {
                if (!ScreenCameraEnabled) return "由系统选择共享屏幕或窗口（Android 16+）";
                return CameraRequested ? "请在系统屏幕共享对话框中选择屏幕或窗口"
                    : "等待 Android 16+ 应用打开相机，届时选择共享屏幕或窗口";
            }
            return ScreenCameraEnabled ? "等待 Android 16+ 应用打开相机" : "共享镜像窗口背后的桌面内容（Android 16+）";
        }
    }

    public void SetScreenCameraRunning(bool running)
    {
        ScreenCameraRunning = running;
    }

    public async Task SendCameraFrameAsync(byte[] jpeg, CancellationToken ct)
    {
        if (_session is { } session && CanControl)
        {
            await session.SendCameraFrameAsync(jpeg, ct).ConfigureAwait(true);
        }
    }
}
