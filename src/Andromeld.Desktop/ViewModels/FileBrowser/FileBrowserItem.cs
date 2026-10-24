using System.Collections.ObjectModel;

using Andromeld.Protocol;

using Lucide.Avalonia;

using ReactiveUI.SourceGenerators;

namespace Andromeld.Desktop.ViewModels;

/// <summary>A UI-thread-owned row with its own remote path, including expanded descendants.</summary>
public sealed partial class FileBrowserItem(FileEntry entry, string path, int depth = 0) : ViewModelBase
{
    public FileEntry Entry { get; } = entry;
    public string Path { get; } = path;
    public string Name => Entry.Name;
    public bool IsDir => Entry.IsDir;
    public LucideIconKind IconKind => FileTypeIcons.For(Entry);
    public string SizeText => Entry.SizeText;
    public string Modified => Entry.Modified;
    public double Indent => Depth * 20;
    public int Depth { get; } = depth;
    public ObservableCollection<FileBrowserItem> Children { get; } = [];
    internal bool ChildrenLoaded { get; set; }

    [Reactive]
    public partial bool IsExpanded { get; internal set; }
    [Reactive]
    public partial bool IsLoading { get; internal set; }
}
