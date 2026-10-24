using Andromeld.Protocol;

using Avalonia.Threading;

namespace Andromeld.Desktop.Services;

/// <summary>UI-thread owned settings, shared by all mirror windows. Device keys are authenticated hashes.</summary>
public sealed class MirrorDisplayPreferences(Settings? settings = null)
{
    private readonly Settings _settings = settings ?? new Settings();
    public event EventHandler<MirrorDisplayChangedEventArgs>? Changed;

    public int GetZoom(string deviceKey, PhoneApp app)
    {
        Dispatcher.UIThread.VerifyAccess();
        return _settings.MirrorDisplays.TryGetValue(deviceKey, out MirrorDisplaySettings? display) &&
               display.ZoomPercent.TryGetValue(AppKey(app), out int zoom) && zoom is >= 50 and <= 150
            ? zoom
            : 100;
    }

    public bool GetImmersiveTitleBar(string deviceKey, PhoneApp app)
    {
        Dispatcher.UIThread.VerifyAccess();
        return _settings.MirrorDisplays.TryGetValue(deviceKey, out MirrorDisplaySettings? display) &&
               display.ImmersiveTitleBars.TryGetValue(AppKey(app), out bool enabled)
            ? enabled
            : !app.Game;
    }

    public bool GetScreenCamera(string deviceKey, PhoneApp app)
    {
        Dispatcher.UIThread.VerifyAccess();
        return _settings.MirrorDisplays.TryGetValue(deviceKey, out MirrorDisplaySettings? display) &&
               display.ScreenCameras.TryGetValue(AppKey(app), out bool enabled) && enabled;
    }

    public MirrorWindowSettings? GetWindow(string deviceKey, PhoneApp app)
    {
        Dispatcher.UIThread.VerifyAccess();
        return _settings.MirrorDisplays.TryGetValue(deviceKey, out MirrorDisplaySettings? display) &&
               display.Windows.TryGetValue(AppKey(app), out MirrorWindowSettings? window) && window is { IsValid: true }
            ? window : null;
    }

    public MirrorCompatibility GetCompatibility(string deviceKey, string package)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!_settings.MirrorDisplays.TryGetValue(deviceKey, out MirrorDisplaySettings? display))
        {
            return MirrorCompatibility.Off;
        }

        if (display.AllAppsCompatible)
        {
            return MirrorCompatibility.All;
        }

        return display.CompatiblePackages.Contains(package) ? MirrorCompatibility.App : MirrorCompatibility.Off;
    }

    public void SetZoom(string deviceKey, PhoneApp app, int zoom)
    {
        Dispatcher.UIThread.VerifyAccess();
        ArgumentOutOfRangeException.ThrowIfLessThan(zoom, 50);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(zoom, 150);
        GetOrCreate(deviceKey).ZoomPercent[AppKey(app)] = zoom;
        SaveAndNotify(deviceKey);
    }

    public void SetImmersiveTitleBar(string deviceKey, PhoneApp app, bool enabled)
    {
        Dispatcher.UIThread.VerifyAccess();
        GetOrCreate(deviceKey).ImmersiveTitleBars[AppKey(app)] = enabled;
        SaveAndNotify(deviceKey);
    }

    public void SetScreenCamera(string deviceKey, PhoneApp app, bool enabled)
    {
        Dispatcher.UIThread.VerifyAccess();
        GetOrCreate(deviceKey).ScreenCameras[AppKey(app)] = enabled;
        SaveAndNotify(deviceKey);
    }

    public void SetWindow(string deviceKey, PhoneApp app, MirrorWindowSettings window)
    {
        Dispatcher.UIThread.VerifyAccess();
        ArgumentNullException.ThrowIfNull(window);
        if (!window.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(window));
        }

        GetOrCreate(deviceKey).Windows[AppKey(app)] = window;
        SaveAndNotify(deviceKey);
    }

    public void SetCompatibility(string deviceKey, string package, MirrorCompatibility mode)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        MirrorDisplaySettings display = GetOrCreate(deviceKey);
        display.AllAppsCompatible = mode == MirrorCompatibility.All;
        if (mode == MirrorCompatibility.App)
        {
            display.CompatiblePackages.Add(package);
        }
        else if (mode == MirrorCompatibility.Off)
        {
            display.CompatiblePackages.Remove(package);
        }

        // Switching away from All preserves other enabled packages, matching the Web menu.
        SaveAndNotify(deviceKey);
    }

    private MirrorDisplaySettings GetOrCreate(string deviceKey)
    {
        if (_settings.MirrorDisplays.TryGetValue(deviceKey, out MirrorDisplaySettings? display))
        {
            return display;
        }

        display = new MirrorDisplaySettings();
        _settings.MirrorDisplays.Add(deviceKey, display);
        return display;
    }

    private void SaveAndNotify(string deviceKey)
    {
        // An omitted Settings instance is a transient owner, with no file to overwrite.
        try { settings?.Save(); }
        finally
        {
            Changed?.Invoke(this, new MirrorDisplayChangedEventArgs(deviceKey));
        } // Keep the current windows consistent even if persistence fails.
    }

    private static string AppKey(PhoneApp app)
    {
        return $"{app.PackageName}:{app.UserId}";
    }
}
