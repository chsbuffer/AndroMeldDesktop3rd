using System.Diagnostics;

using Andromeld.Desktop.Services;

namespace Andromeld.Desktop.ViewModels;

public sealed partial class MirrorViewModel
{
    public MirrorWindowSettings? WindowSettings => App is { } app ? _displayPreferences.GetWindow(_displayDeviceKey, app) : null;

    public void SaveWindowSettings(MirrorWindowSettings window)
    {
        if (App is not { } app)
        {
            return;
        }

        try { _displayPreferences.SetWindow(_displayDeviceKey, app, window); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Closing must still finish; persistence failures cannot leave capture/HWNDs alive.
            Debug.WriteLine($"窗口大小未能保存：{ex.Message}");
        }
    }
}
