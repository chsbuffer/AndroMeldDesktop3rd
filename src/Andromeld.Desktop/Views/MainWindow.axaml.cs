using Andromeld.Desktop.Services;
using Andromeld.Desktop.ViewModels;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

using ReactiveUI.Primitives;
using FluentAvalonia.UI.Controls;

namespace Andromeld.Desktop.Views;

/// <summary>UI-thread window coordinator; the model owns the connection and file/media operations.</summary>
public partial class MainWindow : Window
{
    private readonly Dictionary<MirrorViewModel, MirrorWindow> _mirrorWindows = [];
    private readonly Dictionary<TerminalViewModel, TerminalWindow> _terminalWindows = [];
    private readonly Dictionary<string, PackageInstallerWindow> _installerWindows = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private Point _pressPosition;
    private ListBoxItem? _pressedApp;
    private MainViewModel? _subscribed;

    public MainWindow()
    {
        InitializeComponent();
        InitializeSearchInput();
        DesktopPlatform.ConfigureWindow(this, "Main");
        DataContextChanged += (_, _) =>
        {
            if (_subscribed is not null)
            {
                _subscribed.SetActiveClipboard(null);
                _subscribed.MirrorRequested -= ShowMirror;
                _subscribed.FilesRequested -= ShowFiles;
                _subscribed.TerminalRequested -= ShowTerminal;
            }

            CloseAuxiliaryWindows();
            _subscribed = DataContext as MainViewModel;
            if (_subscribed is not null)
            {
                _subscribed.MirrorRequested += ShowMirror;
                _subscribed.FilesRequested += ShowFiles;
                _subscribed.TerminalRequested += ShowTerminal;
            }

            UpdateClipboard();
        };
        AppList.AddHandler(PointerPressedEvent, AppPointerPressed, RoutingStrategies.Bubble, true);
        AppList.AddHandler(PointerReleasedEvent, AppPointerReleased, RoutingStrategies.Bubble, true);
        AppList.KeyDown += AppKeyDown;
        Opened += (_, _) => (DataContext as MainViewModel)?.StartDiscovery();
        PropertyChanged += ClipboardWindowPropertyChanged;
    }

    public IReadOnlyCollection<MirrorWindow> MirrorWindows => _mirrorWindows.Values;
    public IReadOnlyCollection<TerminalWindow> TerminalWindows => _terminalWindows.Values;
    public FileBrowserWindow? FileWindow { get; private set; }
    public IReadOnlyCollection<PackageInstallerWindow> InstallerWindows => _installerWindows.Values;
    public PackageInstallerWindow? InstallerWindow => _installerWindows.Values.LastOrDefault();

    public async Task ShowPackageInstallerAsync(string path)
    {
        if (_subscribed is not { } main) return;
        path = Path.GetFullPath(path);
        if (_installerWindows.TryGetValue(path, out var existing)) { ActivateWindow(existing); return; }
        var model = new PackageInstallerViewModel(main);
        var window = new PackageInstallerWindow { DataContext = model };
        _installerWindows.Add(path, window);
        TrackAuxiliaryWindow(window);
        window.Closed += (_, _) => _installerWindows.Remove(path);
        window.Show(this);
        await model.LoadAsync(path);
    }

    private static void ActivateWindow(Window window)
    {
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
    }

    private void ShowDeviceClick(object? sender, RoutedEventArgs e)
    {
        ShowDevice();
    }

    public void ShowDevice()
    {
        if (_subscribed is { } model)
        {
            model.SelectedPage = WorkspacePage.Devices;
        }
    }

    public void ShowSettings()
    {
        if (_subscribed is { } model)
        {
            model.SelectedPage = WorkspacePage.Settings;
        }
    }

    public async Task ShowDevelopmentNoticeAsync()
    {
        if (_subscribed is not { NeedsDevelopmentNotice: true } model) return;
        try
        {
            var dialog = new DevelopmentNoticeDialog();
            if (await dialog.ShowAsync((TopLevel)this).ConfigureAwait(true) == FAContentDialogResult.Primary)
                model.AcknowledgeDevelopmentNotice();
        }
        catch (Exception ex) { model.Report(ex); }
    }

    private void ShowFiles()
    {
        if (_subscribed is null)
        {
            return;
        }

        if (FileWindow is { } existing)
        {
            ActivateWindow(existing);
            return;
        }

        FileBrowserWindow window = new() { DataContext = _subscribed.FileBrowser };
        FileWindow = window;
        TrackAuxiliaryWindow(window);
        window.Closed += (_, _) => FileWindow = null;
        window.Show();
    }

    private void TrackAuxiliaryWindow(Window window)
    {
        window.PropertyChanged += ClipboardWindowPropertyChanged;
        window.Closed += (_, _) =>
        {
            window.PropertyChanged -= ClipboardWindowPropertyChanged;
            UpdateClipboard();
        };
    }

    private void CloseAuxiliaryWindows()
    {
        foreach (PackageInstallerWindow window in _installerWindows.Values.ToArray()) window.Close();
        FileWindow?.Close();
        foreach (TerminalWindow window in _terminalWindows.Values.ToArray())
        {
            window.Close();
        }
    }

    private Task ShowTerminal(TerminalViewModel model)
    {
        if (_terminalWindows.TryGetValue(model, out TerminalWindow? existing))
        {
            ActivateWindow(existing);
            return Task.CompletedTask;
        }

        TerminalWindow window = new(model);
        _terminalWindows.Add(model, window);
        TrackAuxiliaryWindow(window);
        window.Closed += (_, _) =>
        {
            _terminalWindows.Remove(model);
            UpdateClipboard();
        };
        window.Show();
        window.UpdateLayout();
        return Task.CompletedTask;
    }

    private async Task ShowMirror(MirrorViewModel model)
    {
        if (_mirrorWindows.TryGetValue(model, out MirrorWindow? existing))
        {
            ActivateWindow(existing);
            return;
        }

        MirrorWindow window = new(model, this);
        _mirrorWindows.Add(model, window);
        window.PropertyChanged += ClipboardWindowPropertyChanged;
        window.Closed += (_, _) =>
        {
            window.PropertyChanged -= ClipboardWindowPropertyChanged;
            _mirrorWindows.Remove(model);
            UpdateClipboard();
        };
        window.Show();
        await window.WaitForViewportAsync();
    }

    private void ClipboardWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        // Activated fires before Avalonia sets IsActive. Observe the updated property so
        // the clipboard and audio owners follow the window that actually gained focus.
        if (e.Property != IsActiveProperty)
        {
            return;
        }

        if (sender is MirrorWindow { IsActive: true, DataContext: MirrorViewModel mirror })
        {
            _subscribed?.SetActiveMirror(mirror);
        }

        UpdateClipboard();
    }

    private void UpdateClipboard()
    {
        Window? active = IsActive ? this : _mirrorWindows.Values.FirstOrDefault(static w => w.IsActive);
        if (active is null && FileWindow is { IsActive: true } file)
        {
            active = file;
        }

        active ??= _terminalWindows.Values.FirstOrDefault(static w => w.IsActive);
        active ??= _installerWindows.Values.FirstOrDefault(static w => w.IsActive);
        _subscribed?.SetActiveClipboard(active?.Clipboard);
    }

    private void AppPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _pressedApp = e.GetCurrentPoint(AppList).Properties.IsLeftButtonPressed ? AppItem(e.Source) : null;
        _pressPosition = e.GetPosition(AppList);
    }

    private async void AppPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        ListBoxItem? item = _pressedApp;
        _pressedApp = null;
        Point movement = e.GetPosition(AppList) - _pressPosition;
        if (e.InitialPressMouseButton != MouseButton.Left || item is null || item != AppItem(e.Source) ||
            Math.Abs(movement.X) > 6 || Math.Abs(movement.Y) > 6 || item.DataContext is not AppTile tile)
        {
            return;
        }

        if (_subscribed is { } model)
        {
            model.SelectedApp = tile;
        }

        _searchSelection = tile;
        await OpenAppAsync();
    }

    private static ListBoxItem? AppItem(object? source)
    {
        return source is Visual visual
            ? visual as ListBoxItem ?? visual.GetVisualAncestors().OfType<ListBoxItem>().FirstOrDefault()
            : null;
    }

    private async void AppKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        await OpenAppAsync();
    }

    private async Task OpenAppAsync()
    {
        if (_subscribed is not { CanMirrorApp: true } model)
        {
            return;
        }

        try { await model.OpenSelectedAppCommand.Execute().ToTask(); }
        catch (OperationCanceledException)
        {
            /* Closing or disconnecting cancels opening. */
        }
        catch (Exception ex) { model.Report(ex); }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (!e.Cancel)
        {
            DesktopPlatform.ClearWindowIdentity(this);
        }
    }

    protected override async void OnClosed(EventArgs e)
    {
        CloseAuxiliaryWindows();
        foreach (MirrorWindow window in _mirrorWindows.Values.ToArray())
        {
            window.Close();
        }

        _subscribed?.SetActiveClipboard(null);
        if (_subscribed is not null)
        {
            _subscribed.MirrorRequested -= ShowMirror;
            _subscribed.FilesRequested -= ShowFiles;
            _subscribed.TerminalRequested -= ShowTerminal;
        }

        base.OnClosed(e);
        if (DataContext is MainViewModel model)
        {
            await model.DisposeAsync();
        }
    }
}
