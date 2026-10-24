using Andromeld.Media;

using ReactiveUI.SourceGenerators;

namespace Andromeld.Desktop.ViewModels;

public sealed partial class MirrorViewModel
{
    public bool CanScreenshot => !IsPhone && CanControl && ShowVideo;
    // The associated window is the sole UI owner of the save picker.
    public Func<DecodedFrame, Task>? SaveScreenshot { private get; set; }

    [ReactiveCommand(CanExecute = nameof(_canScreenshot))]
    private async Task ScreenshotAsync()
    {
        // The decoder publishes independent BGRA buffers. Keep the frame selected before the picker opens.
        if (!CanScreenshot || Frame is not { } frame || SaveScreenshot is not { } save)
        {
            return;
        }

        try { await save(frame).ConfigureAwait(true); }
        catch (OperationCanceledException)
        {
            /* A canceled save does not affect the mirror. */
        }
        catch (Exception ex)
        {
            if (!IsDisposed)
            {
                Error = $"截图保存失败：{ex.Message}";
            }
        }
    }
}