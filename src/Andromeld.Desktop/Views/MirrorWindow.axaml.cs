using System.ComponentModel;
using System.Diagnostics;

using Andromeld.Desktop.Services;
using Andromeld.Desktop.ViewModels;
using Andromeld.Protocol;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;

namespace Andromeld.Desktop.Views;

public partial class MirrorWindow : Window
{
    public static readonly DirectProperty<MirrorWindow, Bitmap?> TitleIconProperty =
        AvaloniaProperty.RegisterDirect<MirrorWindow, Bitmap?>(nameof(TitleIcon), static window => window.TitleIcon);
    private readonly MirrorViewModel _model;
    private readonly DispatcherTimer _resizeTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly DispatcherTimer _contextReleaseTimer = new();
    private readonly TaskCompletionSource _viewportReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _lastMove;
    private long _contextPressStarted;
    private int _lastX, _lastY;
    private bool _pointerDown;
    private bool _pendingContextRelease;
    private MouseButton _pointerButton;
    private IPointer? _pressedPointer;
    private bool _resizeInFlight;
    private Bitmap? _titleIcon;
    private WindowState _stateBeforeFullScreen;

    public MirrorWindow(MirrorViewModel model, Window? owner = null, IDesktopCameraSource? cameraSource = null)
    {
        _model = model;
        _cameraSource = cameraSource;
        DisplayMenu.SetModel(model, this);
        InitializeComponent();
        DesktopPlatform.ConfigureWindow(this, model.IsPhone ? "Phone" : "App");
        UpdateAppIcon();
        DataContext = model;
        InitializeCaption();
        InitializeFileDrag();
        if (model.IsPhone)
        {
            MinHeight = 420;
        }
        else
        {
            // The decoration underlay paints the frame; keep the outer shadow area transparent.
            Background = Brushes.Transparent;
            WindowDecorationsTheme = (ControlTheme)this.FindResource("ApplicationMirrorDecorations")!;
            ExtendClientAreaToDecorationsHint = true;
        }

        UpdateContentInsets();
        InitializeWindowSize(owner);

        model.CloseRequested += Close;
        model.SaveScreenshot = SaveScreenshotAsync;
        Screen.PointerPressed += PointerPressedOnScreen;
        Screen.PointerMoved += PointerMovedOnScreen;
        Screen.PointerReleased += PointerReleasedOnScreen;
        Screen.PointerCaptureLost += PointerCaptureLostOnScreen;
        Screen.PointerWheelChanged += WheelOnScreen;
        Screen.KeyDown += KeyOnScreen;
        AddHandler(KeyDownEvent, NavigationKeyDown, RoutingStrategies.Tunnel);
        Screen.TextInput += TextOnScreen;
        Deactivated += async (_, _) =>
        {
            Screen.ResetInputMethod();
            await CancelPointerAsync();
        };
        Viewport.SizeChanged += (_, _) => ScheduleResize();
        ScalingChanged += (_, _) => ScheduleResize();
        Opened += (_, _) =>
        {
            UpdateLayout();
            Focus();
            ScheduleResize();
            InitializeScreenCamera();
        };
        _model.PropertyChanged += ModelPropertyChanged;
        _model.DisplayConfigurationChanged += ScheduleResize;
        _resizeTimer.Tick += ResizeTick;
        _contextReleaseTimer.Tick += ContextReleaseTick;
    }

    public MirrorDisplayMenu DisplayMenu { get; } = new();
    public Bitmap? TitleIcon { get => _titleIcon; private set => SetAndRaise(TitleIconProperty, ref _titleIcon, value); }

    public Task WaitForViewportAsync()
    {
        return _model.IsPhone ? Task.CompletedTask : _viewportReady.Task;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty && _model is not null && !_model.IsPhone)
        {
            WindowState previous = change.GetOldValue<WindowState>();
            WindowState current = change.GetNewValue<WindowState>();
            if (current == WindowState.Maximized || (current == WindowState.Normal &&
                (previous != WindowState.FullScreen || _stateBeforeFullScreen != WindowState.Maximized)))
            {
                _windowedState = current;
            }
            if (current == WindowState.FullScreen && previous is WindowState.Normal or WindowState.Maximized)
            {
                _stateBeforeFullScreen = previous;
            }
            else if (previous == WindowState.FullScreen && current == WindowState.Normal &&
                     _stateBeforeFullScreen == WindowState.Maximized)
            {
                // The framework's fullscreen button exits to Normal. Restore after its
                // window-state notifications finish, including the native style change.
                Dispatcher.UIThread.Post(() =>
                {
                    if (!_model.IsDisposed && WindowState == WindowState.Normal)
                    {
                        SetCurrentValue(WindowStateProperty, WindowState.Maximized);
                    }
                });
            }
        }

        if ((change.Property == WindowDecorationMarginProperty || change.Property == WindowStateProperty) && MirrorContent is not null)
        {
            UpdateContentInsets();
        }
    }

    public void Minimize()
    {
        if (CanMinimize)
        {
            SetCurrentValue(WindowStateProperty, WindowState.Minimized);
        }
    }

    private void UpdateContentInsets()
    {
        Thickness margin = WindowDecorationMargin;
        // This decoration theme has symmetric frame/shadow edges. Only its titlebar
        // overlaps the video; keep the actual outer frame outside the virtual display.
        double inset = _model.IsPhone || _model.HasError || !_model.ImmersiveTitleBarEnabled || WindowState == WindowState.FullScreen
            ? 0 : Math.Max(0, margin.Top - margin.Bottom);
        MirrorContent.Margin = new Thickness(margin.Left, margin.Top - inset, margin.Right, margin.Bottom);
        Screen.OverlayTop = inset;
        _model.TitleBarHeight = inset;
        UpdateCaptionTheme();
        ScheduleResize();
    }

    private void ModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(MirrorViewModel.CanControl), StringComparison.Ordinal) && _model.CanControl)
        {
            ScheduleResize();
        }

        if (string.Equals(e.PropertyName, nameof(MirrorViewModel.AppIconPng), StringComparison.Ordinal))
        {
            UpdateAppIcon();
        }

        if (e.PropertyName is nameof(MirrorViewModel.HasError) or nameof(MirrorViewModel.ImmersiveTitleBarEnabled))
        {
            UpdateContentInsets();
        }

        if (e.PropertyName is nameof(MirrorViewModel.ScreenCameraEnabled) or nameof(MirrorViewModel.CameraRequested)
            or nameof(MirrorViewModel.CanControl))
        {
            RefreshScreenCamera();
        }
    }

    private void UpdateAppIcon()
    {
        if (_model.AppIconPng is not { } icon)
        {
            return;
        }

        using MemoryStream stream = new(icon, false);
        Icon = new WindowIcon(stream);
        stream.Position = 0;
        Bitmap? old = TitleIcon;
        TitleIcon = new Bitmap(stream);
        old?.Dispose();
    }

    private void ScheduleResize(object? sender = null, EventArgs? e = null)
    {
        if (_model.IsPhone || _model.IsDisposed)
        {
            return;
        }

        if (Viewport.Bounds.Width <= 0 || Viewport.Bounds.Height <= 0)
        {
            return;
        }

        _model.ViewportSize = MirrorSize.FromViewport(Viewport.Bounds.Width, Viewport.Bounds.Height, RenderScaling);
        _resizeTimer.Stop();
        _resizeTimer.Start();
    }

    private async void ResizeTick(object? sender, EventArgs e)
    {
        _resizeTimer.Stop();
        // Wayland confirms maximized bounds and client decorations asynchronously after Show().
        // Reuse the resize debounce so open-window also receives a settled viewport.
        RememberNormalWindowSize();
        _viewportReady.TrySetResult();
        if (_resizeInFlight)
        {
            ScheduleResize();
            return;
        }

        if (!_model.CanControl || WindowState == WindowState.Minimized)
        {
            return;
        }

        _resizeInFlight = true;
        try
        {
            // Use the whole video viewport, including letterboxing from the previous resolution.
            await _model.ResizeAsync();
        }
        finally { _resizeInFlight = false; }
    }

    private async void PointerPressedOnScreen(object? sender, PointerPressedEventArgs e)
    {
        if (!_model.CanControl || _pointerDown || _nativeDrag)
        {
            return;
        }

        Screen.Focus();
        PointerPointProperties properties = e.GetCurrentPoint(Screen).Properties;
        if (properties.IsRightButtonPressed && _model.Shortcuts.RightClickBackEnabled)
        {
            e.Handled = true;
            await _model.SendKeyAsync(4);
            return;
        }

        if ((!properties.IsLeftButtonPressed && !properties.IsRightButtonPressed) ||
            !Screen.TryMap(e.GetPosition(Screen), false, out PixelPoint pixel))
        {
            return;
        }

        _pointerDown = true;
        _pointerButton = properties.IsRightButtonPressed ? MouseButton.Right : MouseButton.Left;
        _pressedPointer = e.Pointer;
        _contextPressStarted = Stopwatch.GetTimestamp();
        _lastX = pixel.X;
        _lastY = pixel.Y;
        _dragPress = e;
        e.Pointer.Capture(Screen);
        e.Handled = true;
        await _model.SendPointerAsync("down", pixel.X, pixel.Y);
    }

    private async void PointerMovedOnScreen(object? sender, PointerEventArgs e)
    {
        if (!_model.CanControl || _nativeDrag || _pendingContextRelease ||
            Stopwatch.GetElapsedTime(_lastMove).TotalMilliseconds < 12 ||
            !Screen.TryMap(e.GetPosition(Screen), _pointerDown, out PixelPoint pixel))
        {
            return;
        }

        if (!_pointerDown)
        {
            PointerPointProperties properties = e.GetCurrentPoint(Screen).Properties;
            if (e.Pointer.Type == PointerType.Touch || properties.IsLeftButtonPressed || properties.IsRightButtonPressed ||
                properties.IsMiddleButtonPressed)
            {
                return;
            }

            _lastMove = Stopwatch.GetTimestamp();
            await _model.SendPointerAsync("hover", pixel.X, pixel.Y);
            return;
        }

        if (e.Pointer != _pressedPointer)
        {
            return;
        }

        _lastMove = Stopwatch.GetTimestamp();
        _lastX = pixel.X;
        _lastY = pixel.Y;
        e.Handled = true;
        await _model.SendPointerAsync("move", pixel.X, pixel.Y);
    }

    private async void PointerReleasedOnScreen(object? sender, PointerReleasedEventArgs e)
    {
        PointerUpdateKind release = e.GetCurrentPoint(Screen).Properties.PointerUpdateKind;
        PointerUpdateKind expected = _pointerButton == MouseButton.Right ? PointerUpdateKind.RightButtonReleased : PointerUpdateKind.LeftButtonReleased;
        if (!_pointerDown || _pendingContextRelease || e.Pointer != _pressedPointer || release != expected)
        {
            return;
        }

        e.Handled = true;
        if (Screen.TryMap(e.GetPosition(Screen), true, out PixelPoint pixel))
        {
            _lastX = pixel.X;
            _lastY = pixel.Y;
        }

        // Web holds a quick right click for at least 600ms to open Android's long-press menu.
        double remaining = 600 - Stopwatch.GetElapsedTime(_contextPressStarted).TotalMilliseconds;
        if (_pointerButton == MouseButton.Right && remaining > 0)
        {
            _pendingContextRelease = true;
            _dragPress = null;
            e.Pointer.Capture(null);
            _contextReleaseTimer.Interval = TimeSpan.FromMilliseconds(remaining);
            _contextReleaseTimer.Start();
            return;
        }

        await FinishPointerAsync("up");
    }

    private async void PointerCaptureLostOnScreen(object? sender, PointerCaptureLostEventArgs e)
    {
        if (!_pendingContextRelease)
        {
            await CancelPointerAsync();
        }
    }

    private async void ContextReleaseTick(object? sender, EventArgs e)
    {
        await FinishPointerAsync("up");
    }

    private Task CancelPointerAsync() => FinishPointerAsync("cancel");

    private async Task FinishPointerAsync(string action)
    {
        _contextReleaseTimer.Stop();
        _pendingContextRelease = false;
        if (!_pointerDown)
        {
            return;
        }

        _pointerDown = false;
        _dragPress = null;
        IPointer? pointer = _pressedPointer;
        _pressedPointer = null;
        pointer?.Capture(null);
        await _model.SendPointerAsync(action, _lastX, _lastY);
    }

    private async void WheelOnScreen(object? sender, PointerWheelEventArgs e)
    {
        if (!Screen.TryMap(e.GetPosition(Screen), false, out PixelPoint pixel))
        {
            return;
        }

        e.Handled = true;
        await _model.SendScrollAsync(pixel.X, pixel.Y, e.Delta.X, e.Delta.Y);
    }

    private async void TextOnScreen(object? sender, TextInputEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text) || !_model.CanControl)
        {
            return;
        }

        e.Handled = true;
        await _model.SendTextAsync(e.Text);
    }

    private async void KeyOnScreen(object? sender, KeyEventArgs e)
    {
        if (Screen.IsComposing)
        {
            e.Handled = true;
            return;
        }

        int code = e.Key switch
        {
            Key.Enter => 66, Key.Back => 67, Key.Delete => 112,
            Key.Tab => 61, Key.Up => 19, Key.Down => 20, Key.Left => 21, Key.Right => 22,
            Key.Home => 122, Key.End => 123, Key.PageUp => 92, Key.PageDown => 93,
            _ => 0
        };
        int meta = 0;
        if ((e.KeyModifiers & KeyModifiers.Shift) != KeyModifiers.None)
        {
            meta |= 1;
        }

        if ((e.KeyModifiers & KeyModifiers.Alt) != KeyModifiers.None)
        {
            meta |= 2;
        }

        if ((e.KeyModifiers & KeyModifiers.Control) != KeyModifiers.None)
        {
            meta |= 4096;
        }

        if ((e.KeyModifiers & KeyModifiers.Meta) != KeyModifiers.None)
        {
            meta |= 65536;
        }

        string keyName = e.Key.ToString();
        if (code == 0 && (meta & (4096 | 65536)) != 0 && keyName.Length == 1 && keyName[0] is >= 'A' and <= 'Z')
        {
            code = keyName[0] - 'A' + 29;
        }

        if (code == 0 || !_model.CanControl)
        {
            return;
        }

        e.Handled = true;
        await _model.SendKeyAsync(code, meta);
    }

    private async void NavigationKeyDown(object? sender, KeyEventArgs e)
    {
        if (DisplayMenu.IsOpen)
        {
            return; // Esc dismisses the local menu before returning in Android.
        }

        if (Screen.IsComposing)
        {
            e.Handled = true;
            return;
        }

        if (!_model.CanControl)
        {
            return;
        }

        int code = e.Key == Key.Escape ? 4 : 0;
        if (_model.IsPhone && e.KeyModifiers == _model.Shortcuts.SelectedModifier.Modifiers)
        {
            code = e.Key switch { Key.R => 187, Key.H => 3, _ => code };
        }

        if (code == 0)
        {
            return;
        }

        // Handle before the focused button/video: no duplicate command or remote text input.
        // Win32 also uses Handled to suppress the WM_CHAR generated by Shift-only shortcuts.
        e.Handled = true;
        await _model.SendKeyAsync(code).ConfigureAwait(false);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (!e.Cancel && _camera is not null && !_cameraClosed)
        {
            if (_cameraClosing)
            {
                e.Cancel = true;
                return;
            }

            _cameraClosing = true;
            ValueTask cleanup = _camera.DisposeAsync();
            if (cleanup.IsCompletedSuccessfully)
            {
                _cameraClosed = true;
            }
            else
            {
                e.Cancel = true;
                _ = CloseAfterCameraAsync(cleanup);
                return;
            }
        }

        if (!e.Cancel)
        {
            SaveWindowSize();
            DesktopPlatform.ClearWindowIdentity(this);
        }
    }

    protected override async void OnClosed(EventArgs e)
    {
        _screenshotClosed = true;
        _model.SaveScreenshot = null;
        await CancelPointerAsync();
        _contextReleaseTimer.Tick -= ContextReleaseTick;
        _viewportReady.TrySetCanceled();
        _resizeTimer.Stop();
        _resizeTimer.Tick -= ResizeTick;
        _model.PropertyChanged -= ModelPropertyChanged;
        _model.DisplayConfigurationChanged -= ScheduleResize;
        Screen.PropertyChanged -= CaptionSourceChanged;
        DisplayMenu.Hide();
        _model.CloseRequested -= Close;
        _model.DragOutStarted -= StartFileDrag;
        if (_camera is not null)
        {
            await _camera.DisposeAsync();
        }

        Bitmap? titleIcon = TitleIcon;
        TitleIcon = null;
        titleIcon?.Dispose();
        base.OnClosed(e);
        await _model.DisposeAsync();
    }
}
