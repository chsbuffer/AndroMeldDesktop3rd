using Andromeld.Protocol;

using Lucide.Avalonia;

namespace Andromeld.Desktop.ViewModels;

internal static class FileTypeIcons
{
    internal static LucideIconKind For(FileEntry entry)
    {
        if (entry.IsDir) return LucideIconKind.Folder;
        string extension = Path.GetExtension(entry.Name).ToLowerInvariant();
        return extension switch
        {
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp" or ".svg" or ".heic" or ".avif" or ".tif" or ".tiff" => LucideIconKind.FileImage,
            ".mp4" or ".mkv" or ".mov" or ".avi" or ".webm" or ".3gp" or ".ts" or ".m4v" => LucideIconKind.Film,
            ".mp3" or ".flac" or ".wav" or ".ogg" or ".m4a" or ".aac" or ".opus" or ".wma" => LucideIconKind.FileMusic,
            ".zip" or ".rar" or ".7z" or ".tar" or ".gz" or ".bz2" or ".xz" or ".tgz" => LucideIconKind.FileArchive,
            ".apk" or ".apks" => LucideIconKind.Package,
            ".cs" or ".java" or ".kt" or ".kts" or ".js" or ".mjs" or ".ts" or ".tsx" or ".jsx" or ".py" or ".rs" or ".go" or ".c" or ".h" or ".cpp" or ".hpp"
                or ".css" or ".scss" or ".html" or ".htm" or ".sql" or ".sh" or ".ps1" => LucideIconKind.FileCode,
            ".json" or ".jsonc" => LucideIconKind.FileBraces,
            ".xls" or ".xlsx" or ".ods" or ".csv" or ".tsv" => LucideIconKind.FileSpreadsheet,
            ".ppt" or ".pptx" or ".odp" or ".key" => LucideIconKind.FileChartColumn,
            ".ttf" or ".otf" or ".woff" or ".woff2" => LucideIconKind.FileType,
            ".txt" or ".md" or ".markdown" or ".log" or ".pdf" or ".rtf" or ".doc" or ".docx" or ".odt" or ".epub" or ".mobi" or ".azw3" => LucideIconKind.FileText,
            _ => FromMime(entry.Mime)
        };
    }

    private static LucideIconKind FromMime(string? mime)
    {
        if (string.IsNullOrEmpty(mime)) return LucideIconKind.File;
        if (mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return LucideIconKind.FileImage;
        if (mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase)) return LucideIconKind.Film;
        if (mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)) return LucideIconKind.FileMusic;
        if (mime.StartsWith("font/", StringComparison.OrdinalIgnoreCase)) return LucideIconKind.FileType;
        if (mime.StartsWith("text/", StringComparison.OrdinalIgnoreCase)) return LucideIconKind.FileText;
        if (string.Equals(mime, "application/pdf", StringComparison.OrdinalIgnoreCase)) return LucideIconKind.FileText;
        if (mime.Contains("zip", StringComparison.OrdinalIgnoreCase) || mime.Contains("rar", StringComparison.OrdinalIgnoreCase)) return LucideIconKind.FileArchive;
        if (string.Equals(mime, "application/vnd.android.package-archive", StringComparison.OrdinalIgnoreCase)) return LucideIconKind.Package;
        return LucideIconKind.File;
    }
}
