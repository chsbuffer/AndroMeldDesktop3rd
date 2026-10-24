using System.Diagnostics;

using Andromeld.Platform.Linux;
using Andromeld.Platform.Windows;

using Avalonia.Controls;
using Avalonia.Input;

namespace Andromeld.Desktop.Services;

/// <summary>UI owns native callbacks; metadata and sequential file I/O run independently on a worker.</summary>
public sealed class FileDragBatch : IAsyncDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "AndroMeld", "drag", Guid.NewGuid().ToString("N"));
    private readonly Task<FileDragSource[]> _sources;
    private readonly CancellationTokenSource _stop;
    private Task<string[]>? _files;

    public FileDragBatch(Func<CancellationToken, Task<FileDragSource[]>> sources, CancellationToken ct)
    {
        _stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _sources = Task.Run(() => sources(_stop.Token), _stop.Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        try { await _sources.ConfigureAwait(false); }
        catch (Exception ex) { Debug.WriteLine(ex.Message); }

        if (_files is not null)
        {
            try { await _files.ConfigureAwait(false); }
            catch (Exception ex) { Debug.WriteLine(ex.Message); }
        }

        try
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Debug.WriteLine(ex.Message); }

        _stop.Dispose();
    }

    private static string[] Names(FileDragSource[] sources)
    {
        HashSet<string> used = new(StringComparer.OrdinalIgnoreCase);
        return sources.Select(source =>
        {
            string safe = SafeName(source.Name);
            string candidate = safe;
            int copy = 2;
            while (!used.Add(candidate))
            {
                candidate = NumberedName(safe, copy++);
            }

            return candidate;
        }).ToArray();
    }

    private static string SafeName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string safe = new string(name.Select(c => invalid.Contains(c) || c is '/' or '\\' ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (safe.Length > 180)
        {
            safe = safe[..180];
        }

        if (safe.Length == 0 || safe is "." or "..")
        {
            safe = "Android file";
        }

        string stem = Path.GetFileNameWithoutExtension(safe);
        if (new[]
            {
                "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3",
                "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
            }.Contains(stem, StringComparer.OrdinalIgnoreCase))
        {
            safe = "_" + safe;
        }

        return safe;
    }

    private static string NumberedName(string safe, int copy) =>
        $"{Path.GetFileNameWithoutExtension(safe)} ({copy}){Path.GetExtension(safe)}";

    private IReadOnlyList<PromisedFile> GetMetadata()
    {
        FileDragSource[] sources = _sources.WaitAsync(TimeSpan.FromSeconds(35), _stop.Token).GetAwaiter().GetResult();
        string[] names = Names(sources);
        return sources.Select((s, i) => new PromisedFile(names[i], s.Size)).ToArray();
    }

    private Task<string[]> FilesAsync()
    {
        return _files ??= Task.Run(async () =>
        {
            FileDragSource[] sources = await _sources.ConfigureAwait(false);
            string[] names = Names(sources);
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(_directory);
            else Directory.CreateDirectory(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            List<string> paths = new();
            for (int index = 0; index < sources.Length; index++)
            {
                string path = Path.Combine(_directory, names[index]);
                string partial = path + ".part";
                await using (FileStream stream = new(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                {
                    await sources[index].Receive(stream, _stop.Token).ConfigureAwait(false);
                }

                _stop.Token.ThrowIfCancellationRequested();
                File.Move(partial, path);
                paths.Add(path);
            }

            return paths.ToArray();
        }, _stop.Token);
    }

    internal Task CopyToDirectoryAsync(string directory) => Task.Run(async () =>
    {
        string targetDirectory = Path.GetFullPath(directory);
        if (!Path.IsPathFullyQualified(directory) || !Directory.Exists(targetDirectory))
            throw new IOException("请选择现存的本机文件夹。");

        FileDragSource[] sources = await _sources.ConfigureAwait(false);
        string[] names = sources.Select(static source => SafeName(source.Name)).ToArray();
        HashSet<string> used = new(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < sources.Length; index++)
        {
            _stop.Token.ThrowIfCancellationRequested();
            string partial = Path.Combine(targetDirectory, ".andromeld-" + Guid.NewGuid().ToString("N") + ".part");
            try
            {
                FileStreamOptions options = new() { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                await using (FileStream stream = new(partial, options))
                    await sources[index].Receive(stream, _stop.Token).ConfigureAwait(false);
                _stop.Token.ThrowIfCancellationRequested();
                MoveUnique(partial, targetDirectory, names[index], used);
            }
            finally { TryDelete(partial); }
        }
    }, _stop.Token);

    private static void MoveUnique(string partial, string directory, string name, HashSet<string> used)
    {
        int copy = 1;
        while (true)
        {
            string candidate = copy == 1 ? name : NumberedName(name, copy);
            if (used.Add(candidate))
            {
                string target = Path.Combine(directory, candidate);
                try
                {
                    File.Move(partial, target);
                    return;
                }
                catch (IOException) when (File.Exists(target) || Directory.Exists(target))
                {
                    // A concurrent copy already took this name; reserve it and try the next number.
                }
            }
            copy++;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Debug.WriteLine(ex.Message); }
    }

    public async Task<bool> RunAsync(Window window, PointerPressedEventArgs press)
    {
        if (OperatingSystem.IsWindowsVersionAtLeast(6, 1) && window.TryGetPlatformHandle() is { HandleDescriptor: "HWND" } handle)
        {
            return ShellFileDrag.Run(handle.Handle, GetMetadata,
                index => FilesAsync().GetAwaiter().GetResult()[index], _stop.Token);
        }

        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("文件拖出仅支持 Windows 和 Linux KDE Wayland。");
        if (!string.Equals(((TopLevel)window).PlatformImpl?.GetType().Namespace, "Avalonia.Wayland", StringComparison.Ordinal))
            throw new IOException("Linux 文件拖出仅支持 KDE Wayland。");

        SynchronizationContext owner = SynchronizationContext.Current ??
            throw new InvalidOperationException("文件拖出必须在 UI 线程启动。");
        using var kde = await KdeArkDropService.TryCreateAsync(owner, _stop.Token);
        if (kde is null) throw new IOException("未找到 KDE 拖放服务；Linux 文件拖出仅支持 KDE Wayland。");
        using var transfer = KdeArkDropTransfer.Create(kde);
        bool kdeAccepted = await DragDrop.DoDragDropAsync(press, transfer, DragDropEffects.Copy) == DragDropEffects.Copy;
        if (!kdeAccepted) return false;
        string directory = await kde.WaitForDestinationAsync(_stop.Token);
        await CopyToDirectoryAsync(directory);
        return true;
    }
}
