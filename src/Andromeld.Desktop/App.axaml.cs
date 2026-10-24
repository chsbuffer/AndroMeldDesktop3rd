using Andromeld.Desktop.ViewModels;
using Andromeld.Desktop.Views;
using Andromeld.Desktop.Services;

using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Andromeld.Desktop;

public class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow { DataContext = new MainViewModel() };
            window.Opened += MainWindowOpened;
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async void MainWindowOpened(object? sender, EventArgs e)
    {
        if (sender is not MainWindow window)
        {
            return;
        }

        window.Opened -= MainWindowOpened;
        await window.ShowDevelopmentNoticeAsync().ConfigureAwait(true);
        if (!window.IsVisible) return;
        if (Program.Instance is { } instance)
        {
            using var lifetime = new CancellationTokenSource();
            void Closed(object? source, EventArgs args) => lifetime.Cancel();
            window.Closed += Closed;
            try
            {
                await foreach (string? path in instance.Requests.ReadAllAsync(lifetime.Token).ConfigureAwait(true))
                {
                    window.Show();
                    if (window.WindowState == Avalonia.Controls.WindowState.Minimized) window.WindowState = Avalonia.Controls.WindowState.Normal;
                    window.Activate();
                    if (path is not null) await window.ShowPackageInstallerAsync(path).ConfigureAwait(true);
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { /* The main window closed. */ }
            catch (Exception ex) { (window.DataContext as MainViewModel)?.Report(ex); }
            finally { window.Closed -= Closed; }
        }
        else if (Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { Args.Length: > 0 } desktop)
            await window.ShowPackageInstallerAsync(desktop.Args[0]).ConfigureAwait(true);
    }
}
