using System.Runtime.InteropServices;

using Andromeld.Media;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Andromeld.Desktop.Controls;

/// <summary>UI-thread video presentation and input mapping. Overlaid video uses one Avalonia surface.</summary>
public sealed class VideoView : Grid
{
    public static readonly StyledProperty<DecodedFrame?> FrameProperty = AvaloniaProperty.Register<VideoView, DecodedFrame?>(nameof(Frame));
    public static readonly StyledProperty<bool> UseNativeRenderingProperty =
        AvaloniaProperty.Register<VideoView, bool>(nameof(UseNativeRendering), true);
    public static readonly StyledProperty<double> OverlayTopProperty = AvaloniaProperty.Register<VideoView, double>(nameof(OverlayTop),
        validate: static value => double.IsFinite(value) && value >= 0);
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly VideoInputMethodClient _inputMethod;
    private WriteableBitmap? _bitmap;
    private SdlVideoHost? _native;

    public VideoView()
    {
        Background = new SolidColorBrush(Color.Parse("#F5F5F7"));
        Focusable = true;
        ClipToBounds = true;
        Children.Add(_image);
        _inputMethod = new VideoInputMethodClient(this);
        Children.Add(_inputMethod.CompositionPopup);
        TextInputMethodClientRequested += (_, e) => e.Client = _inputMethod;
        LostFocus += (_, _) => ResetInputMethod();
        SizeChanged += (_, _) =>
        {
            Present();
            _inputMethod.UpdateCursor();
        };
    }

    public string Backend => _native?.IsReady == true ? "SDL3" : "Avalonia";
    public string? NativeError { get; private set; }
    internal bool IsComposing => _inputMethod.IsComposing;

    internal void ResetInputMethod() => _inputMethod.Reset();
    public DecodedFrame? Frame { get => GetValue(FrameProperty); set => SetValue(FrameProperty, value); }
    /// <summary>Disable for video with Avalonia overlays so the entire frame shares their compositor.</summary>
    public bool UseNativeRendering { get => GetValue(UseNativeRenderingProperty); set => SetValue(UseNativeRenderingProperty, value); }
    /// <summary>UI-thread owned titlebar overlap in DIP; input still maps to the full video viewport.</summary>
    public double OverlayTop { get => GetValue(OverlayTopProperty); set => SetValue(OverlayTopProperty, value); }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateBackend();
        Present();
    }

    private void UpdateBackend()
    {
        if (!UseNativeRendering && _native is { } previous)
        {
            Children.Remove(previous);
            _native = null;
        }

        if (!UseNativeRendering || NativeError is not null)
        {
            return;
        }

        string? descriptor = TopLevel.GetTopLevel(this)?.TryGetPlatformHandle()?.HandleDescriptor;
        if (_native is null && descriptor is "HWND" or "XID")
        {
            _native = new SdlVideoHost();
            _native.Failed += ex => Dispatcher.UIThread.Post(() =>
            {
                NativeError = ex.Message;
                if (_native is { } native)
                {
                    Children.Remove(native);
                    _native = null;
                }

                _image.IsVisible = true;
                Present();
            });
            Children.Add(_native);
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == UseNativeRenderingProperty)
        {
            UpdateBackend();
        }

        if (change.Property == FrameProperty || change.Property == UseNativeRenderingProperty)
        {
            Present();
        }
    }

    private void Present()
    {
        if (Frame is not { } frame)
        {
            return;
        }

        if (_native is { } native)
        {
            native.Present(frame);
            _image.IsVisible = false;
            return;
        }

        // Submit one full frame. SDL and Avalonia present independently, so copying
        // the same decoded frame into both cannot keep the title strip and body in sync.
        _image.IsVisible = true;
        if (_bitmap is null || _bitmap.PixelSize != new PixelSize(frame.Width, frame.Height))
        {
            _image.Source = null;
            _bitmap?.Dispose();
            _bitmap = new WriteableBitmap(new PixelSize(frame.Width, frame.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            _image.Source = _bitmap;
        }

        using (ILockedFramebuffer pixels = _bitmap.Lock())
        {
            for (int y = 0; y < frame.Height; y++)
            {
                Marshal.Copy(frame.Bgra, y * frame.Width * 4, IntPtr.Add(pixels.Address, y * pixels.RowBytes), frame.Width * 4);
            }
        }

        _image.InvalidateVisual();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ResetInputMethod();
        base.OnDetachedFromVisualTree(e);
        _image.Source = null;
        _bitmap?.Dispose();
        _bitmap = null;
        if (_native is { } native)
        {
            Children.Remove(native);
            _native = null;
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        _inputMethod.SetAnchor(e.GetPosition(this));
        base.OnPointerPressed(e);
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        _inputMethod.SetPreeditText(null);
        base.OnTextInput(e);
    }

    public bool TryMap(Point point, bool clamp, out PixelPoint pixel)
    {
        pixel = default;
        if (Frame is not { } frame || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return false;
        }

        double scale = Math.Min(Bounds.Width / frame.Width, Bounds.Height / frame.Height);
        double x = (point.X - ((Bounds.Width - (frame.Width * scale)) / 2)) / scale;
        double y = (point.Y - ((Bounds.Height - (frame.Height * scale)) / 2)) / scale;
        if (!clamp && (x < 0 || y < 0 || x >= frame.Width || y >= frame.Height))
        {
            return false;
        }

        pixel = new PixelPoint(Math.Clamp((int)x, 0, frame.Width - 1), Math.Clamp((int)y, 0, frame.Height - 1));
        return true;
    }
}
