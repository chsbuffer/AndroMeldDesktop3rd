using Andromeld.Desktop.Controls;
using Andromeld.Media;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace Andromeld.Desktop.Views;

public partial class MirrorWindow
{
    private static readonly double[] ChannelLuminance = CreateChannelLuminance();
    public static readonly DirectProperty<MirrorWindow, ThemeVariant> CaptionThemeProperty =
        AvaloniaProperty.RegisterDirect<MirrorWindow, ThemeVariant>(nameof(CaptionTheme), static window => window.CaptionTheme);
    public static readonly DirectProperty<MirrorWindow, IBrush?> CaptionBackgroundProperty =
        AvaloniaProperty.RegisterDirect<MirrorWindow, IBrush?>(nameof(CaptionBackground), static window => window.CaptionBackground);
    private readonly SolidColorBrush _sampledCaptionBackground = new();
    private ThemeVariant _captionTheme = ThemeVariant.Light;
    private IBrush? _captionBackground;
    private bool _hasCaptionSample;
    public ThemeVariant CaptionTheme { get => _captionTheme; private set => SetAndRaise(CaptionThemeProperty, ref _captionTheme, value); }
    public IBrush? CaptionBackground { get => _captionBackground; private set => SetAndRaise(CaptionBackgroundProperty, ref _captionBackground, value); }

    private void InitializeCaption()
    {
        Screen.PropertyChanged += CaptionSourceChanged;
        UpdateCaptionTheme();
    }

    private void CaptionSourceChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == VideoView.FrameProperty || e.Property == VideoView.OverlayTopProperty ||
            e.Property == BoundsProperty || e.Property == IsVisibleProperty)
        {
            UpdateCaptionTheme();
        }
    }

    // UI thread only, using the same immutable frame and mapping as VideoView. Sample
    // 32 x 4 points in the actual titlebar strip, including its light letterbox areas.
    // No desktop capture, full-frame scan, allocation or delayed theme change per frame.
    private void UpdateCaptionTheme()
    {
        if (_model.IsPhone)
        {
            return;
        }

        if (WindowState == WindowState.FullScreen || !Screen.IsVisible || Screen.Frame is not { } frame ||
            Screen.Bounds.Width <= 0 || Screen.Bounds.Height <= 0)
        {
            _hasCaptionSample = false;
            CaptionTheme = ActualThemeVariant;
            CaptionBackground = this.FindResource("AppBackground") as IBrush;
            return;
        }

        if (!_model.ImmersiveTitleBarEnabled)
        {
            UpdateNonImmersiveCaption(frame);
            return;
        }

        if (Screen.OverlayTop <= 0)
        {
            _hasCaptionSample = false;
            CaptionTheme = ActualThemeVariant;
            return;
        }

        double luminance = 0d;
        double height = Math.Min(Screen.OverlayTop, Screen.Bounds.Height);
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 32; x++)
            {
                byte r = 245, g = 245, b = 247; // VideoView's letterbox background.
                if (Screen.TryMap(new Point((x + .5) * Screen.Bounds.Width / 32, (y + .5) * height / 4), false, out PixelPoint pixel))
                {
                    int offset = ((pixel.Y * frame.Width) + pixel.X) * 4;
                    b = frame.Bgra[offset];
                    g = frame.Bgra[offset + 1];
                    r = frame.Bgra[offset + 2];
                }

                luminance += (.2126 * ChannelLuminance[r]) + (.7152 * ChannelLuminance[g]) + (.0722 * ChannelLuminance[b]);
            }
        }

        SetCaptionLuminance(luminance / 128);
    }

    private void UpdateNonImmersiveCaption(DecodedFrame frame)
    {
        // Sample only the image's top strip, excluding letterboxing. A coarse RGB
        // histogram chooses the predominant color rather than averaging text/icons into it.
        double scale = Math.Min(Screen.Bounds.Width / frame.Width, Screen.Bounds.Height / frame.Height);
        double titleHeight = Math.Max(1, WindowDecorationMargin.Top - WindowDecorationMargin.Bottom);
        int height = Math.Clamp((int)Math.Ceiling(titleHeight / scale), 1, frame.Height);
        Span<int> histogram = stackalloc int[512 * 4];
        histogram.Clear();
        Span<int> counts = histogram.Slice(0, 512);
        Span<int> reds = histogram.Slice(512, 512);
        Span<int> greens = histogram.Slice(1024, 512);
        Span<int> blues = histogram.Slice(1536, 512);
        int dominant = 0;
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 32; x++)
            {
                int offset = ((((2 * y + 1) * height / 8) * frame.Width) + ((2 * x + 1) * frame.Width / 64)) * 4;
                byte b = frame.Bgra[offset], g = frame.Bgra[offset + 1], r = frame.Bgra[offset + 2];
                int bucket = ((r >> 5) << 6) | ((g >> 5) << 3) | (b >> 5);
                counts[bucket]++;
                reds[bucket] += r;
                greens[bucket] += g;
                blues[bucket] += b;
                if (counts[bucket] > counts[dominant])
                {
                    dominant = bucket;
                }
            }
        }

        Color color = Color.FromRgb((byte)(reds[dominant] / counts[dominant]), (byte)(greens[dominant] / counts[dominant]),
            (byte)(blues[dominant] / counts[dominant]));
        Color previous = _sampledCaptionBackground.Color;
        if (previous.A != 255 || Math.Max(Math.Abs(color.R - previous.R), Math.Max(Math.Abs(color.G - previous.G), Math.Abs(color.B - previous.B))) >= 4)
        {
            _sampledCaptionBackground.Color = color;
        }

        CaptionBackground = _sampledCaptionBackground;
        color = _sampledCaptionBackground.Color;
        SetCaptionLuminance((.2126 * ChannelLuminance[color.R]) + (.7152 * ChannelLuminance[color.G]) + (.0722 * ChannelLuminance[color.B]));
    }

    private void SetCaptionLuminance(double luminance)
    {
        double threshold = .18;
        if (_hasCaptionSample)
        {
            threshold = CaptionTheme == ThemeVariant.Dark ? .205 : .155;
        }

        CaptionTheme = luminance < threshold ? ThemeVariant.Dark : ThemeVariant.Light;
        _hasCaptionSample = true;
    }

    private static double[] CreateChannelLuminance()
    {
        double[] values = new double[256];
        for (int i = 0; i < values.Length; i++)
        {
            double channel = i / 255d;
            values[i] = channel <= .04045 ? channel / 12.92 : Math.Pow((channel + .055) / 1.055, 2.4);
        }

        return values;
    }
}
