using Andromeld.Packages;

using Avalonia.Media.Imaging;

using SkiaSharp;

namespace Andromeld.Desktop.Services;

internal static class PackageIconRenderer
{
    public static Bitmap? Render(PackageInfo info)
    {
        if (info.Icon is { } bytes)
        {
            using var source = new MemoryStream(bytes);
            return new Bitmap(source);
        }
        if (info.VectorIcon is not { } vector) return null;
        using var surface = SKSurface.Create(new SKImageInfo(192, 192));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.Scale(192 / vector.Width, 192 / vector.Height);
        foreach (var item in vector.Paths)
        {
            using var path = SKPath.ParseSvgPathData(item.Data);
            if (path is null) continue;
            var m = item.Transform;
            path.Transform(new SKMatrix(m.M11, m.M21, m.M31, m.M12, m.M22, m.M32, 0, 0, 1));
            path.FillType = item.EvenOdd ? SKPathFillType.EvenOdd : SKPathFillType.Winding;
            var color = new SKColor(item.Color);
            using var paint = new SKPaint { IsAntialias = true, Color = color.WithAlpha((byte)(color.Alpha * item.Alpha)) };
            canvas.DrawPath(path, paint);
        }
        using var image = surface.Snapshot();
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = encoded.AsStream();
        return new Bitmap(stream);
    }
}
