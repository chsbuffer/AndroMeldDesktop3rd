using System.Collections.ObjectModel;

using ReactiveUI;
using ReactiveUI.SourceGenerators;

namespace Andromeld.Desktop.ViewModels;

public sealed partial class FileBrowserViewModel
{
    [Reactive]
    public partial FileSortField SortField { get; private set; } = FileSortField.Name;

    [Reactive]
    public partial bool IsSortDescending { get; private set; }

    public bool IsNameSort => SortField == FileSortField.Name;
    public bool IsSizeSort => SortField == FileSortField.Size;
    public bool IsModifiedSort => SortField == FileSortField.Modified;
    public bool IsSortAscending => !IsSortDescending;
    public bool IsNameAscending => IsNameSort && IsSortAscending;
    public bool IsNameDescending => IsNameSort && IsSortDescending;
    public bool IsSizeAscending => IsSizeSort && IsSortAscending;
    public bool IsSizeDescending => IsSizeSort && IsSortDescending;
    public bool IsModifiedAscending => IsModifiedSort && IsSortAscending;
    public bool IsModifiedDescending => IsModifiedSort && IsSortDescending;

    [ReactiveCommand]
    private void SortByName() => ToggleSort(FileSortField.Name);

    [ReactiveCommand]
    private void SortBySize() => ToggleSort(FileSortField.Size);

    [ReactiveCommand]
    private void SortByModified() => ToggleSort(FileSortField.Modified);

    [ReactiveCommand]
    private void ChooseNameSort() => ChooseSort(FileSortField.Name);

    [ReactiveCommand]
    private void ChooseSizeSort() => ChooseSort(FileSortField.Size);

    [ReactiveCommand]
    private void ChooseModifiedSort() => ChooseSort(FileSortField.Modified);

    [ReactiveCommand]
    private void SortAscending() => SetSortDirection(descending: false);

    [ReactiveCommand]
    private void SortDescending() => SetSortDirection(descending: true);

    [ReactiveCommand(CanExecute = nameof(_canUseFiles))]
    private void SelectAll() => SelectAllVisible();

    public void SelectAllVisible()
    {
        if (!CanUseFiles || VisibleFiles.Count == 0) return;
        using (Selection.BatchUpdate())
        {
            Selection.Clear();
            for (int index = 0; index < VisibleFiles.Count; index++)
            {
                Selection.Select(index);
            }
        }
    }

    private void ToggleSort(FileSortField field)
    {
        if (SortField == field)
        {
            IsSortDescending = !IsSortDescending;
        }
        else
        {
            SortField = field;
            IsSortDescending = false;
        }

        ApplySort();
    }

    private void ChooseSort(FileSortField field)
    {
        if (SortField == field) return;
        SortField = field;
        IsSortDescending = false;
        ApplySort();
    }

    private void SetSortDirection(bool descending)
    {
        if (IsSortDescending == descending) return;
        IsSortDescending = descending;
        ApplySort();
    }

    private void ApplySort()
    {
        FileBrowserItem[] selected = Selection.SelectedItems.OfType<FileBrowserItem>().ToArray();
        RebuildVisibleFiles();
        using (Selection.BatchUpdate())
        {
            Selection.Clear();
            foreach (FileBrowserItem item in selected)
            {
                int index = VisibleFiles.IndexOf(item);
                if (index >= 0) Selection.Select(index);
            }
        }

        this.RaisePropertyChanged(nameof(IsNameSort));
        this.RaisePropertyChanged(nameof(IsSizeSort));
        this.RaisePropertyChanged(nameof(IsModifiedSort));
        this.RaisePropertyChanged(nameof(IsSortAscending));
        this.RaisePropertyChanged(nameof(IsSortDescending));
        this.RaisePropertyChanged(nameof(IsNameAscending));
        this.RaisePropertyChanged(nameof(IsNameDescending));
        this.RaisePropertyChanged(nameof(IsSizeAscending));
        this.RaisePropertyChanged(nameof(IsSizeDescending));
        this.RaisePropertyChanged(nameof(IsModifiedAscending));
        this.RaisePropertyChanged(nameof(IsModifiedDescending));
    }

    private void SortFileTree()
    {
        _fileItems.Sort(CompareFileItems);
        foreach (FileBrowserItem item in _fileItems)
        {
            SortChildren(item);
        }
    }

    private void SortChildren(FileBrowserItem parent)
    {
        if (!parent.ChildrenLoaded) return;
        SortCollection(parent.Children);
        foreach (FileBrowserItem child in parent.Children)
        {
            SortChildren(child);
        }
    }

    private void SortCollection(ObservableCollection<FileBrowserItem> items)
    {
        if (items.Count < 2) return;
        List<FileBrowserItem> sorted = [.. items];
        sorted.Sort(CompareFileItems);
        for (int index = 0; index < sorted.Count; index++)
        {
            if (ReferenceEquals(items[index], sorted[index])) continue;
            int previous = items.IndexOf(sorted[index]);
            if (previous >= 0) items.Move(previous, index);
        }
    }

    private int CompareFileItems(FileBrowserItem left, FileBrowserItem right)
    {
        if (left.IsDir != right.IsDir) return left.IsDir ? -1 : 1;
        int comparison = SortField switch
        {
            FileSortField.Size => left.Entry.Size.CompareTo(right.Entry.Size),
            FileSortField.Modified => left.Entry.Mtime.CompareTo(right.Entry.Mtime),
            _ => StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name)
        };
        if (comparison != 0) return IsSortDescending ? -comparison : comparison;
        int name = StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name);
        return name != 0 ? name : StringComparer.Ordinal.Compare(left.Name, right.Name);
    }
}
