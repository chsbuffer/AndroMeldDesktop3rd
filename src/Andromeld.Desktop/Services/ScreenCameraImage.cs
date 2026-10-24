using Andromeld.Platform.Windows;

using SkiaSharp;

namespace Andromeld.Desktop.Services;

public static class ScreenCameraImage
{
    public static byte[] Encode(DesktopCaptureFrame frame)
        => Encode(frame.Width, frame.Height, frame.Bgra);

    public static byte[] Encode(int width, int height, byte[] bgra)
    {
        using SKImage image = SKImage.FromPixelCopy(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque), bgra);
        // The helper declares SENSOR_ORIENTATION=90 and draws JPEGs directly onto its
        // 1280x720 surface. Supply sensor-oriented pixels, not an EXIF rotation hint.
        var sensorWidth = height;
        var sensorHeight = width;
        using SKSurface surface = SKSurface.Create(new SKImageInfo(sensorWidth, sensorHeight, SKColorType.Bgra8888, SKAlphaType.Opaque));
        surface.Canvas.Translate(0, width);
        surface.Canvas.RotateDegrees(-90);
        surface.Canvas.DrawImage(image, 0, 0, SKSamplingOptions.Default);
        using SKImage sensorImage = surface.Snapshot();
        using SKData? encoded = sensorImage.Encode(SKEncodedImageFormat.Jpeg, 60);
        if (encoded is null || encoded.Size > 1048576)
        {
            throw new IOException("屏幕摄像头图像超过 1 MiB。");
        }

        return encoded.ToArray();
    }
}
