using Andromeld.Desktop.ViewModels;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Andromeld.Desktop.Views;

public partial class MainWindow
{
    private bool _searchNavigation;
    private object? _searchSelection;

    private void InitializeSearchInput()
    {
        AppSearch.AddHandler(KeyDownEvent, SearchKeyDown, RoutingStrategies.Tunnel);
        ApplicationsPage.KeyDown += (_, e) =>
        {
            if (!e.Handled && e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
            {
                SearchKeyDown(AppSearch, e);
            }
        };
        foreach (Button button in new[] { SearchPhone, SearchFiles, SearchTerminal, SearchAction })
        {
            button.KeyDown += SearchKeyDown;
            button.PointerEntered += (_, _) => SelectSearchCandidate(button);
        }

        AppSearch.PointerPressed += (_, _) => _searchNavigation = false;
        AppList.SelectionChanged += (_, _) =>
        {
            if (AppList.SelectedItem is AppTile app)
            {
                _searchSelection = app;
            }
        };
        AppList.PointerMoved += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(_subscribed?.Search) && AppItem(e.Source) is { DataContext: AppTile app } item &&
                !ReferenceEquals(_searchSelection, app))
            {
                SelectSearchCandidate(item);
            }
        };
        AppSearch.TextChanged += (_, _) =>
        {
            _searchNavigation = false;
            foreach (Button button in new[] { SearchPhone, SearchFiles, SearchTerminal, SearchAction })
            {
                button.Classes.Remove("searchSelected");
            }

            Dispatcher.UIThread.Post(static state => ((MainWindow)state!).RefreshSearchSelection(), this);
        };
    }

    private void RefreshSearchSelection()
    {
        if (!ApplicationsPage.IsEffectivelyVisible || _subscribed is null || string.IsNullOrWhiteSpace(_subscribed.Search))
        {
            return;
        }

        List<Control> candidates = SearchCandidates();
        Control? selected = candidates.Find(c => Equals(SearchCandidateKey(c), _searchSelection)) ?? candidates.FirstOrDefault();
        if (selected is not null)
        {
            SelectSearchCandidate(selected);
        }
    }

    private List<Control> SearchCandidates()
    {
        UpdateLayout();
        List<Control> candidates = [];
        foreach (Button button in new[] { SearchPhone, SearchFiles, SearchTerminal })
        {
            if (button.IsEffectivelyVisible)
            {
                candidates.Add(button);
            }
        }

        for (int index = 0; index < AppList.ItemCount; index++)
        {
            if (AppList.ContainerFromIndex(index) is ListBoxItem item)
            {
                candidates.Add(item);
            }
        }

        if (SearchAction.IsEffectivelyVisible)
        {
            candidates.Add(SearchAction);
        }

        return candidates;
    }

    private object SearchCandidateKey(Control control)
    {
        if (control is ListBoxItem { DataContext: AppTile app })
        {
            return app;
        }

        if (control == SearchAction)
        {
            return $"action:{_subscribed?.SearchActionLabel}:{_subscribed?.SearchActionText}";
        }

        return control;
    }

    private void SelectSearchCandidate(Control control)
    {
        _searchSelection = SearchCandidateKey(control);
        if (_subscribed is { } model)
        {
            model.SelectedApp = control.DataContext as AppTile;
        }

        foreach (Button button in new[] { SearchPhone, SearchFiles, SearchTerminal, SearchAction })
        {
            button.Classes.Set("searchSelected", button == control);
        }

        control.BringIntoView();
    }

    private void SearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.KeyModifiers != KeyModifiers.None || _subscribed is not { } model)
        {
            return;
        }

        if (AppSearch.IsKeyboardFocusWithin &&
            AppSearch.GetVisualDescendants().OfType<TextPresenter>().Any(static p => !string.IsNullOrEmpty(p.PreeditText)))
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            model.Search = "";
            _searchSelection = null;
            AppSearch.Focus();
            return;
        }

        if (e.Key is not (Key.Up or Key.Down or Key.Left or Key.Right or Key.Enter))
        {
            return;
        }

        if (e.Key is Key.Left or Key.Right && sender == AppSearch && AppSearch.Text?.Length > 0 && !_searchNavigation)
        {
            return;
        }

        List<Control> candidates = SearchCandidates();
        if (candidates.Count == 0)
        {
            return;
        }

        Control selected = candidates.Find(c => Equals(SearchCandidateKey(c), _searchSelection)) ??
                           candidates.Find(c => ReferenceEquals(c.DataContext, model.SelectedApp)) ?? candidates[0];
        e.Handled = true;
        if (e.Key == Key.Enter)
        {
            if (selected is Button { Command: { } command } button)
            {
                if (command.CanExecute(button.CommandParameter))
                {
                    command.Execute(button.CommandParameter);
                }
            }
            else if (selected.DataContext is AppTile app)
            {
                model.SelectedApp = app;
                _ = OpenAppAsync();
            }

            return;
        }

        _searchNavigation = true;
        (Control Control, Point Center)[] positioned = candidates
            .Select(c => (Control: c, Center: c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), this)!.Value)).ToArray();
        (Control Control, Point Center) current = positioned.Single(c => c.Control == selected);
        (Control Control, Point Center)[] row = positioned.Where(c => Math.Abs(c.Center.Y - current.Center.Y) <= 6).OrderBy(static c => c.Center.X)
            .ToArray();
        if (e.Key is Key.Left or Key.Right)
        {
            int index = Array.FindIndex(row, c => c.Control == selected);
            selected = row[(index + (e.Key == Key.Left ? row.Length - 1 : 1)) % row.Length].Control;
        }
        else
        {
            (Control Control, Point Center)[] nextRows =
                positioned.Where(c => e.Key == Key.Up ? c.Center.Y < current.Center.Y - 6 : c.Center.Y > current.Center.Y + 6).ToArray();
            if (nextRows.Length > 0)
            {
                double nearestY = nextRows.Min(c => Math.Abs(c.Center.Y - current.Center.Y));
                selected = nextRows.Where(c => Math.Abs(Math.Abs(c.Center.Y - current.Center.Y) - nearestY) <= 6)
                    .MinBy(c => Math.Abs(c.Center.X - current.Center.X)).Control;
            }
        }

        SelectSearchCandidate(selected);
    }
}