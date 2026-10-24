namespace Andromeld.Protocol;

public sealed record MirrorSize(int Width, int Height, double Dpr)
{
    /// <summary>Match the Web client's even pixels, 240px minimum and 1920px encoding limit.</summary>
    public static MirrorSize FromViewport(double width, double height, double scaling)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || !double.IsFinite(scaling) || width <= 0 || height <= 0 || scaling <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "镜像视口尺寸和缩放必须为正数。");
        }

        double pixelsWide = EvenPixels(width * scaling);
        double pixelsHigh = EvenPixels(height * scaling);
        if (!double.IsFinite(pixelsWide) || !double.IsFinite(pixelsHigh))
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        double factor = Math.Min(1, 1920d / Math.Max(pixelsWide, pixelsHigh));
        return new MirrorSize((int)EvenPixels(pixelsWide * factor), (int)EvenPixels(pixelsHigh * factor), scaling * factor);
    }

    // Math.round for positive Web inputs, including half pixels; round before the encoding cap.
    private static double EvenPixels(double pixels)
    {
        return Math.Max(240, Math.Floor(Math.Floor(pixels + .5) / 2) * 2);
    }
}