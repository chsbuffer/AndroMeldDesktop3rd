using Andromeld.Desktop.Services;
using Andromeld.Protocol;

using Avalonia.Input;
using Avalonia.Platform.Storage;

namespace Andromeld.Desktop.Views;

public partial class MirrorWindow
{
    private PointerPressedEventArgs? _dragPress;
    private bool _nativeDrag, _uploading;

    private void InitializeFileDrag()
    {
        DragDrop.SetAllowDrop(Screen, true);
        Screen.AddHandler(DragDrop.DragOverEvent, FileDragOver);
        Screen.AddHandler(DragDrop.DropEvent, FileDrop);
        _model.DragOutStarted += StartFileDrag;
    }

    private void FileDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = _model.CanControl && !_nativeDrag && !_uploading && e.DataTransfer.Contains(DataFormat.File)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void FileDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!_model.CanControl || _nativeDrag || _uploading)
        {
            return;
        }

        _uploading = true;
        try
        {
            string[] paths = e.DataTransfer.TryGetFiles()?.Select(static f => f.TryGetLocalPath() ?? throw new IOException("请拖入本地文件。")).ToArray() ??
                             [];
            await _model.UploadFilesAsync(paths);
        }
        catch (OperationCanceledException)
        {
            /* Mirror disconnected. */
        }
        catch (Exception ex) { _model.ReportTransfer(ex); }
        finally { _uploading = false; }
    }

    private async void StartFileDrag(DragOutTransfer transfer)
    {
        if (_nativeDrag || !_pointerDown || _dragPress is not { } press || !_model.CanControl)
        {
            try { await transfer.FinishAsync(false, _model.Lifetime); }
            catch (Exception)
            {
                /* Session already gone. */
            }

            return;
        }

        _nativeDrag = true;
        _pointerDown = false;
        _pressedPointer = null;
        _pendingContextRelease = false;
        _contextReleaseTimer.Stop();
        _dragPress = null;
        press.Pointer.Capture(null);
        int x = _lastX;
        int y = _lastY;
        bool success = false;
        try
        {
            await using FileDragBatch batch = new(async ct =>
            {
                await transfer.HandoffAsync(x, y, ct).ConfigureAwait(false);
                DragOutItem[] items = await transfer.Metadata.WaitAsync(TimeSpan.FromSeconds(35), ct).ConfigureAwait(false);
                return items.Select(item => new FileDragSource(item.Name, item.Size,
                    (stream, token) => transfer.ReceiveItemAsync(item, stream, token))).ToArray();
            }, _model.Lifetime);
            success = await batch.RunAsync(this, press);
        }
        catch (OperationCanceledException)
        {
            /* Local cancel, disconnect or window closed. */
        }
        catch (Exception ex) { _model.ReportTransfer(ex); }
        finally
        {
            _nativeDrag = false;
            try
            {
                using CancellationTokenSource stop = new(TimeSpan.FromSeconds(2));
                await transfer.FinishAsync(success, stop.Token);
            }
            catch (Exception)
            {
                /* Session already gone. */
            }
        }
    }
}
