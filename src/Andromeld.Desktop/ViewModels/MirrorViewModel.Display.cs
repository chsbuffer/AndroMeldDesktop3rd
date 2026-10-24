using Andromeld.Cache;
using Andromeld.Desktop.Services;
using Andromeld.Protocol;

using Avalonia.Threading;

using ReactiveUI;
using ReactiveUI.Primitives;
using ReactiveUI.SourceGenerators;

namespace Andromeld.Desktop.ViewModels;

public sealed partial class MirrorViewModel
{
    private IObservable<bool> _canZoomOut = null!, _canZoomIn = null!, _canChangeDisplay = null!;
    private string _displayDeviceKey = "";
    private MirrorDisplayPreferences _displayPreferences = null!;

    [Reactive(nameof(ZoomLabel), nameof(CanZoomOut), nameof(CanZoomIn))]
    public partial int ZoomPercent { get; private set; } = 100;
    public string ZoomLabel => $"{ZoomPercent}%";
    public bool CanZoomOut => CanChangeDisplay && ZoomPercent > 50;
    public bool CanZoomIn => CanChangeDisplay && ZoomPercent < 150;
    public bool CanChangeDisplay => !IsPhone && CanControl;
    [Reactive]
    public partial bool ImmersiveTitleBarEnabled { get; private set; } = true;
    [Reactive(nameof(IsCompatibilityOff), nameof(IsCompatibilityApp), nameof(IsCompatibilityAll))]
    public partial MirrorCompatibility Compatibility { get; private set; }
    public bool IsCompatibilityOff => Compatibility == MirrorCompatibility.Off;
    public bool IsCompatibilityApp => Compatibility == MirrorCompatibility.App;
    public bool IsCompatibilityAll => Compatibility == MirrorCompatibility.All;
    public string CompatibilityAppLabel => App is null ? "为此窗口启用" : $"为{App.DisplayName}启用";
    public MirrorDisplayConfiguration DisplayConfiguration =>
        MirrorDisplayConfiguration.FromViewport(ViewportSize, ZoomPercent, !IsCompatibilityOff, TitleBarHeight);
    public event EventHandler? DisplayConfigurationChanged;

    private void InitializeDisplay(MirrorDisplayPreferences preferences)
    {
        _displayPreferences = preferences;
        if (!IsPhone)
        {
            _displayDeviceKey = AppCache.DeviceKey(_control.DeviceId, _control.Token);
            _displayPreferences.Changed += OnDisplayPreferencesChanged;
            OnDisplayPreferencesChanged(this, new MirrorDisplayChangedEventArgs(_displayDeviceKey));
        }

        _canZoomOut = this.WhenAnyValue(x => x.CanZoomOut);
        PropertyChanged += SaveScreenCameraPreference;
        _canZoomIn = this.WhenAnyValue(x => x.CanZoomIn);
        _canChangeDisplay = this.WhenAnyValue(x => x.CanChangeDisplay);
        foreach (ReactiveCommand<RxVoid, RxVoid> command in new[]
                 {
                     ZoomOutCommand, ZoomInCommand, CompatibilityOffCommand, CompatibilityAppCommand, CompatibilityAllCommand,
                     ToggleImmersiveTitleBarCommand
                 })
        {
            command.ThrownExceptions.Subscribe(ex => Dispatcher.UIThread.Post(static state =>
            {
                (MirrorViewModel model, string message) = ((MirrorViewModel, string))state!;
                if (!model.IsDisposed)
                {
                    model.Error = $"显示设置已生效，但未能保存：{message}";
                }
            }, (this, ex.Message))).DisposeWith(_commands);
            _commands.Add(command);
        }
    }

    private void OnDisplayPreferencesChanged(object? sender, MirrorDisplayChangedEventArgs e)
    {
        Dispatcher.UIThread.VerifyAccess();
        string deviceKey = e.DeviceKey;
        if (IsDisposed || IsPhone || !string.Equals(deviceKey, _displayDeviceKey, StringComparison.Ordinal))
        {
            return;
        }

        if (App is { } app)
        {
            ZoomPercent = _displayPreferences.GetZoom(deviceKey, app);
            ImmersiveTitleBarEnabled = _displayPreferences.GetImmersiveTitleBar(deviceKey, app);
            ScreenCameraEnabled = _displayPreferences.GetScreenCamera(deviceKey, app);
        }

        Compatibility = _displayPreferences.GetCompatibility(deviceKey, App?.PackageName ?? "");
        DisplayConfigurationChanged?.Invoke(this, EventArgs.Empty);
    }

    [ReactiveCommand(CanExecute = nameof(_canZoomOut))]
    private void ZoomOut()
    {
        SetZoom(Math.Max(50, ZoomPercent - 10));
    }

    [ReactiveCommand(CanExecute = nameof(_canZoomIn))]
    private void ZoomIn()
    {
        SetZoom(Math.Min(150, ZoomPercent + 10));
    }

    [ReactiveCommand(CanExecute = nameof(_canChangeDisplay))]
    private void ToggleImmersiveTitleBar()
    {
        if (App is { } app)
        {
            _displayPreferences.SetImmersiveTitleBar(_displayDeviceKey, app, !ImmersiveTitleBarEnabled);
        }
        else
        {
            ImmersiveTitleBarEnabled = !ImmersiveTitleBarEnabled;
        }

        Error = "";
    }

    [ReactiveCommand(CanExecute = nameof(_canChangeDisplay))]
    private void CompatibilityOff()
    {
        SetCompatibility(MirrorCompatibility.Off);
    }

    [ReactiveCommand(CanExecute = nameof(_canChangeDisplay))]
    private void CompatibilityApp()
    {
        SetCompatibility(MirrorCompatibility.App);
    }

    [ReactiveCommand(CanExecute = nameof(_canChangeDisplay))]
    private void CompatibilityAll()
    {
        SetCompatibility(MirrorCompatibility.All);
    }

    private void SetZoom(int zoom)
    {
        if (App is { } app)
        {
            _displayPreferences.SetZoom(_displayDeviceKey, app, zoom);
        }
        else
        {
            ZoomPercent = zoom;
            DisplayConfigurationChanged?.Invoke(this, EventArgs.Empty);
        }

        Error = "";
    }

    private void SetCompatibility(MirrorCompatibility mode)
    {
        if (App is { } app)
        {
            _displayPreferences.SetCompatibility(_displayDeviceKey, app.PackageName, mode);
        }
        else if (mode == MirrorCompatibility.App)
        {
            Compatibility = mode;
            DisplayConfigurationChanged?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            _displayPreferences.SetCompatibility(_displayDeviceKey, "", mode);
        }

        Error = "";
    }
}
