namespace Andromeld.Desktop.Tests;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Contains("--screen-camera"))
        {
            UiSmoke.Run(screenCameraOnly: true);
            return;
        }
        if (args is ["--instance-forward", var identifier, .. var forwarded])
        {
            SingleInstanceChecks.ForwardAsync(identifier, forwarded).GetAwaiter().GetResult();
            return;
        }
        if (args.Contains("--single-instance"))
        {
            SingleInstanceChecks.RunAsync().GetAwaiter().GetResult();
            return;
        }
        if (args.Contains("--installation"))
        {
            UiSmoke.Run(installationOnly: true);
            return;
        }
        if (args.Contains("--clipboard"))
        {
            UiSmoke.Run(native: args.Contains("--native"), clipboardOnly: true);
            return;
        }
        if (args.Contains("--wireless-qr"))
        {
            UiSmoke.Run(wirelessQrOnly: true);
            return;
        }
        if (args.Contains("--mirror-input"))
        {
            UiSmoke.Run(mirrorInputOnly: true);
            return;
        }
        if (args.Contains("--notifications-native"))
        {
            NotificationNativeChecks.Run();
            return;
        }
        if (args.Contains("--notifications"))
        {
            UiSmoke.Run(notificationsOnly: true);
            return;
        }
        if (args.Contains("--adb-path"))
        {
            AdbPathChecks.Run();
            return;
        }
        if (args.Contains("--phone-mirror"))
        {
            UiSmoke.Run(phoneMirrorOnly: true);
            return;
        }
        if (args.Contains("--usb"))
        {
            UiSmoke.Run(usbOnly: true);
            return;
        }
        if (args.Contains("--about"))
        {
            UiSmoke.Run(aboutOnly: true);
            return;
        }
        if (args.Contains("--screen-camera-native"))
        {
            ScreenCameraNativeChecks.Run();
            return;
        }
        if (args.Contains("--file-drag-native"))
        {
            UiSmoke.Run(native: true, fileDragOnly: true);
            return;
        }
        if (args.Contains("--files"))
        {
            UiSmoke.Run(fileDragOnly: true);
            return;
        }
        if (args.Contains("--terminal-native"))
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException("Native terminal checks require Windows.");
            TerminalNativeChecks.Run();
            return;
        }
        if (args.Contains("--win32-input") || args.Contains("--live-input"))
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException("Win32 input checks require Windows 7 or later.");
            var addressIndex = Array.IndexOf(args, "--address");
            var address = addressIndex < 0 ? null : args.ElementAtOrDefault(addressIndex + 1) ?? throw new ArgumentException("--address requires a Web endpoint.");
            WindowsInputChecks.Run(args.Contains("--live-input"), address);
            return;
        }
        var gameIndex = Array.IndexOf(args, "--live-game");
        var game = gameIndex < 0 ? null : args.ElementAtOrDefault(gameIndex + 1) ?? throw new ArgumentException("--live-game requires an application package name.");
        UiSmoke.Run(live: args.Contains("--live") || args.Contains("--live-mirror") || args.Contains("--live-discovery") || args.Contains("--live-clipboard") || game is not null,
            mirror: args.Contains("--live-mirror"), native: args.Contains("--native"), discovery: args.Contains("--live-discovery"), game: game, clipboard: args.Contains("--live-clipboard"),
            missingFfmpeg: args.Contains("--missing-ffmpeg"));
    }
    internal static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
    }
}
