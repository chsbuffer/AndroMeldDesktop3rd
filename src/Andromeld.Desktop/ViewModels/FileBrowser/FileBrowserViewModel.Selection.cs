using ReactiveUI;
using ReactiveUI.SourceGenerators;

namespace Andromeld.Desktop.ViewModels;

public sealed partial class FileBrowserViewModel
{
    [Reactive(nameof(IsDetailsView), nameof(IsIconView), nameof(IsTreeView), nameof(IsListHeaderVisible))]
    public partial FileBrowserViewMode ViewMode { get; set; }

    public bool IsDetailsView => ViewMode == FileBrowserViewMode.Details;
    public bool IsIconView => ViewMode == FileBrowserViewMode.Icons;
    public bool IsTreeView => ViewMode == FileBrowserViewMode.Tree;
    public bool IsListHeaderVisible => !IsIconView;

    [ReactiveCommand]
    private void ShowDetails() => ViewMode = FileBrowserViewMode.Details;

    [ReactiveCommand]
    private void ShowIcons() => ViewMode = FileBrowserViewMode.Icons;

    [ReactiveCommand]
    private void ShowTree() => ViewMode = FileBrowserViewMode.Tree;

    private void NotifySelectionChanged()
    {
        _selectedItems = Selection.SelectedItems.OfType<FileBrowserItem>().ToArray();
        this.RaisePropertyChanged(nameof(SelectedItem));
        this.RaisePropertyChanged(nameof(SelectedItems));
        this.RaisePropertyChanged(nameof(SelectionCount));
        this.RaisePropertyChanged(nameof(HasSelection));
        this.RaisePropertyChanged(nameof(HasSelectedFiles));
        this.RaisePropertyChanged(nameof(CanDownloadSelection));
        this.RaisePropertyChanged(nameof(CanDeleteSelection));
    }
}
