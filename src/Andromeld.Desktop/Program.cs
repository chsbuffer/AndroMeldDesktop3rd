using Andromeld.Desktop.Services;

using Avalonia;

using ReactiveUI.Avalonia;

namespace Andromeld.Desktop;

internal static class Program
{
    internal static DesktopInstance? Instance { get; private set; }

    [STAThread]
    internal static void Main(string[] args)
    {
        using var instance = DesktopPlatform.CreateApplicationInstance();
        string? path = DesktopInstance.GetPackagePath(args);
        if (!instance.IsFirstInstance)
        {
            instance.ForwardAsync(path).GetAwaiter().GetResult();
            return;
        }

        instance.StartListening(path);
        Instance = instance;
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            Instance = null;
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    internal static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UseAndromeldPlatform()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace()
            .UseReactiveUI(static _ => { });
}
