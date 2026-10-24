using Andromeld.Media;

using Avalonia.Platform.Storage;

using SkiaSharp;

namespace Andromeld.Desktop.Views;

public partial class MirrorWindow
{
    private bool _screenshotClosed;

    private async Task SaveScreenshotAsync(DecodedFrame frame)
    {
        DisplayMenu.Hide();
        using IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "保存截图",
            SuggestedFileName = $"AndroMeld_{_model.App?.PackageName ?? "link"}_{DateTime.Now:yyyyMMdd_HHmmss}.png",
            DefaultExtension = "png",
            FileTypeChoices = [new FilePickerFileType("PNG 图像") { Patterns = ["*.png"], MimeTypes = ["image/png"] }],
            ShowOverwritePrompt = true
        }).ConfigureAwait(true);
        if (file is null || _screenshotClosed)
        {
            return;
        }

        // UI owns the picker and frame selection; the worker owns the PNG encoder and its resources.
        byte[] png = await Task.Run(() =>
        {
            using SKImage image = SKImage.FromPixelCopy(new SKImageInfo(frame.Width, frame.Height, SKColorType.Bgra8888, SKAlphaType.Opaque),
                frame.Bgra);
            using SKData? encoded = image.Encode(SKEncodedImageFormat.Png, 100);
            return encoded?.ToArray() ?? throw new IOException("无法编码截图。");
        }, CancellationToken.None).ConfigureAwait(true);
        if (_screenshotClosed)
        {
            return;
        }

        // Once the destination is accepted, finish this independent snapshot even if the mirror closes.
        Stream output = await file.OpenWriteAsync().ConfigureAwait(false);
        await using (output.ConfigureAwait(false))
        {
            if (output.CanSeek)
            {
                output.SetLength(0);
            }

            await output.WriteAsync(png, CancellationToken.None).ConfigureAwait(false);
            await output.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }
}