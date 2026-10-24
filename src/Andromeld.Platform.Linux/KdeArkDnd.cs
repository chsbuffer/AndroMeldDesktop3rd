namespace Andromeld.Platform.Linux;

/// <summary>KIO's Ark drag-and-drop extension used by Dolphin to hand a drop destination back to the source.</summary>
public static class KdeArkDnd
{
    public const string ExtractInterface = "org.kde.ark.DndExtract";
    public const string ExtractMethod = "extractSelectedFilesTo";
    public const string ServiceMimeType = "application/x-kde-ark-dndextract-service";
    public const string PathMimeType = "application/x-kde-ark-dndextract-path";
}
