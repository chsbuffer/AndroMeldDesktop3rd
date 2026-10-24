using Andromeld.Desktop.ViewModels;
using Andromeld.Desktop.Views;
using Andromeld.Platform.Windows;
using Andromeld.Testing;
using Avalonia.Controls;
using Avalonia.Input;
using ReactiveUI.Primitives;

namespace Andromeld.Desktop.Tests;

public static class FileDragChecks
{
    public static async Task RunAsync(MainViewModel model, MainWindow window, MockPhone phone, string temporary, CancellationToken ct)
    {
        await model.ShowFilesCommand.Execute().ToTask();
        FileBrowserViewModel files = model.FileBrowser;
        var fileWindow = window.FileWindow!;
        Program.Check(DragDrop.GetAllowDrop(fileWindow.FindControl<ListBox>("FileList")!), "file browser accepts OS file drops");
        await FileBrowserChecks.RunAsync(files, fileWindow, phone, temporary, ct);
        var path = Path.Combine(temporary, "拖入.bin");
        await File.WriteAllBytesAsync(path, MockPhone.Payload, ct);
        await files.UploadFilesAsync([path]);
        Program.Check(!files.Uploading && files.UploadStatus.Contains("已上传") && phone.Uploads.Last().Data.SequenceEqual(MockPhone.Payload),
            "file drop upload completes, refreshes the destination and restores UI state");
        var second = Path.Combine(temporary, "拖入2.bin");
        await File.WriteAllBytesAsync(second, MockPhone.Payload, ct);
        int uploadsBefore = phone.Uploads.Count;
        await files.UploadFilesAsync([path, second]);
        Program.Check(phone.Uploads.Count == uploadsBefore + 2 && phone.Uploads.Skip(uploadsBefore).All(upload => upload.Data.SequenceEqual(MockPhone.Payload)),
            "multiple dragged files upload sequentially");
        var before = phone.Uploads.Count;
        try { await files.UploadFilesAsync([temporary]); throw new Exception("Directory accepted"); }
        catch (IOException) { Program.Check(phone.Uploads.Count == before, "folder drop is rejected before uploading a partial selection"); }
        phone.RejectUploads = true;
        try { await files.UploadFilesAsync([path]); throw new Exception("Upload failure ignored"); }
        catch (IOException) { Program.Check(!files.Uploading && model.IsConnected, "upload failure restores controls without disconnecting the phone"); }
        finally { phone.RejectUploads = false; }
        if (OperatingSystem.IsWindowsVersionAtLeast(6, 1))
        {
            var reads = 0;
            var data = ShellFileDrag.CreateDataObject(() => [new("中文.bin", MockPhone.Payload.Length), new("empty", 0)],
                index => { reads++; return index == 0 ? path : Path.Combine(temporary, "empty"); });
            await File.WriteAllBytesAsync(Path.Combine(temporary, "empty"), [], ct);
            Program.Check(reads == 0, "native OLE descriptors do not eagerly read file contents");
            var native = ShellFileDragDiagnostics.Read(data, 0);
            var empty = ShellFileDragDiagnostics.Read(data, 1);
            Program.Check(native.Name == "中文.bin" && native.Size == MockPhone.Payload.Length && native.Content.SequenceEqual(MockPhone.Payload) && empty.Content.Length == 0 && reads == 2,
                "Windows FILEGROUPDESCRIPTORW and indexed FILECONTENTS IStream round-trip Unicode, sizes and real native streams");
            if (fileWindow.TryGetPlatformHandle() is { HandleDescriptor: "HWND" } handle)
            {
                using var canceled = new CancellationTokenSource(); canceled.Cancel();
                Program.Check(!ShellFileDrag.Run(handle.Handle, () => [new("中文.bin", MockPhone.Payload.Length)], _ => path, canceled.Token),
                    "real Windows OLE drag loop marshals source interfaces and cancels without copying");
            }
        }
    }
}
