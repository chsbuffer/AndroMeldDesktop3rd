using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

using AdvancedSharpAdbClient;

using Andromeld.Discovery;
using Andromeld.Platform.Windows;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Labs.Notifications;
using Avalonia.Threading;

namespace Andromeld.Desktop.Services;

public static class DesktopPlatform
{
    public static bool IsWayland => OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is not null;
    public static bool SupportsScreenCamera => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) || IsWayland;

    public static IDesktopCameraSource? CreateScreenCameraSource(Window window)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) && window.TryGetPlatformHandle() is { HandleDescriptor: "HWND" } handle)
            return new WindowsDesktopCameraSource(handle.Handle);
        return IsWayland ? new WaylandDesktopCameraSource() : null;
    }

    internal static DesktopInstance CreateApplicationInstance()
    {
        string user = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + Environment.UserName;
        string suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(user))).Substring(0, 16);
        return new DesktopInstance("AndroMeld." + suffix);
    }

    public static IUsbDiscovery CreateUsbDiscovery()
    {
        return new UsbDiscovery(EnsureAdbServerAsync);
    }

    public static IWirelessAdbPairing CreateWirelessAdbPairing() => new WirelessAdbPairing(EnsureAdbServerAsync);

    private static async Task EnsureAdbServerAsync(CancellationToken ct)
    {
        AdbServer server = new();
        if ((await server.GetStatusAsync(ct).ConfigureAwait(false)).IsRunning)
        {
            return;
        }

        string? path = FindAdbExecutable(AppContext.BaseDirectory);
        if (path is null)
        {
            throw new IOException("未找到 ADB，请检查程序目录或将 adb 加入 PATH。");
        }

        await server.StartServerAsync(path, false, ct).ConfigureAwait(false);
        if (!(await server.GetStatusAsync(ct).ConfigureAwait(false)).IsRunning)
        {
            throw new IOException("无法启动 ADB 服务，请检查 platform-tools。");
        }
    }

    internal static string? FindAdbExecutable(string baseDirectory)
    {
        string executable = OperatingSystem.IsWindows() ? "adb.exe" : "adb";
        string? sdk = Environment.GetEnvironmentVariable("ANDROID_HOME") ?? Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT");
        IEnumerable<string?> candidates = new[]
            {
                sdk is null ? null : Path.Combine(sdk, "platform-tools", executable)
            }
            .Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(directory => Path.Combine(directory.Trim('"'), executable)))
            .Concat([
                Path.Combine(baseDirectory, executable),
                Path.Combine(baseDirectory, "runtimes", RuntimeInformation.RuntimeIdentifier, "native", executable)
            ]);
        return candidates.FirstOrDefault(static candidate => !string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate));
    }

    public static AppBuilder UseAndromeldPlatform(this AppBuilder builder)
    {
        builder = builder.UsePlatformDetect();

        if (IsWayland)
        {
            builder = builder.UseWayland();
        }

        if (OperatingSystem.IsWindows())
        {
            return builder.WithAppNotifications(new AppNotificationOptions
            {
                AppName = "AndroMeld Desktop",
                AppUserModelId = "AndroMeld.Desktop.Notifications",
                ClearOnAppClose = true
            });
        }

        if (OperatingSystem.IsLinux())
        {
            builder.AfterSetup(static _ =>
            {
                if (string.IsNullOrEmpty(Tmds.DBus.Protocol.DBusAddress.Session)) return;
                LinuxNotificationManager manager = new();
                NativeNotificationManager.RegisterNativeNotificationManager(manager);
                Dispatcher.UIThread.ShutdownStarted += (_, _) => manager.Dispose();
            });
        }

        return builder;
    }

    public static void ConfigureWindow(Window window, string kind)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (OperatingSystem.IsLinux() && string.Equals(kind, "App", StringComparison.Ordinal))
        {
            // Avalonia 12.1 keeps KWin's server decorations even when client-area
            // extension is requested. A partial-decoration request drops that window's
            // xdg-decoration object permanently; restore Full to draw our complete theme.
            window.WindowDecorations = WindowDecorations.BorderOnly;
            window.WindowDecorations = WindowDecorations.Full;
        }

        if (OperatingSystem.IsWindowsVersionAtLeast(6, 1) && window.TryGetPlatformHandle() is { HandleDescriptor: "HWND" } handle)
        {
            WindowTaskbarIdentity.Set(handle.Handle, $"AndroMeld.Desktop.{kind}.{Guid.NewGuid():N}");
        }
    }

    public static void ClearWindowIdentity(Window window)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (OperatingSystem.IsWindowsVersionAtLeast(6, 1) && window.TryGetPlatformHandle() is { HandleDescriptor: "HWND" } handle)
        {
            WindowTaskbarIdentity.Set(handle.Handle, null);
        }
    }
}
