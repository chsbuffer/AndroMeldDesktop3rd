using Andromeld.Desktop.Services;
using Andromeld.Desktop.ViewModels;

using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Andromeld.Desktop.Views;

public sealed partial class PackageInstallerWindow : Window
{
    public PackageInstallerWindow()
    {
        InitializeComponent();
        DesktopPlatform.ConfigureWindow(this, "PackageInstaller");
    }

    private void CloseClick(object? sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (!e.Cancel) DesktopPlatform.ClearWindowIdentity(this);
    }

    protected override async void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (DataContext is PackageInstallerViewModel model) await model.DisposeAsync();
    }
}
