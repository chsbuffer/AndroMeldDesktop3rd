using Andromeld.Desktop.ViewModels;
using Andromeld.Desktop.Views;
using Andromeld.Testing;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;

using ReactiveUI.Primitives;

namespace Andromeld.Desktop.Tests;

internal static class PackageInstallerChecks
{
    public static async Task RunAsync(MainViewModel main, MainWindow window, MockPhone phone, string temporary, CancellationToken ct)
    {
        phone.SimulatePackageInstaller = true;
        string path = Path.Combine(temporary, "Seaglass 2.4.1.apkm");
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Seaglass", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        byte[] apk = PackageFixture.WithSigningCertificate(PackageFixture.Apk(), certificate.Export(X509ContentType.Cert), 0x7109871a);
        await File.WriteAllBytesAsync(path, PackageFixture.Archive(("base.apk", apk), ("split.en.apk", PackageFixture.Apk("config.en"))), ct);
        await window.ShowPackageInstallerAsync(path);
        var installer = window.InstallerWindow ?? throw new InvalidOperationException("No installer window");
        var model = (PackageInstallerViewModel)installer.DataContext!;
        Program.Check(model is { Name: "Seaglass", PackageName: "org.example.seaglass", Format: "APKM", IsLoading: false, CanInstall: false, HasIcon: true, Certificates.Count: 1 } && model.Permissions.Count == 3,
            "startup file entry shows parsed package metadata while offline and disables installation");
        Program.Check(phone.Uploads.IsEmpty && phone.TerminalInputs.IsEmpty, "opening a package requires explicit confirmation before any phone operation");
        await window.ShowPackageInstallerAsync(path);
        Program.Check(window.InstallerWindows.Count == 1 && ReferenceEquals(window.InstallerWindow, installer), "reopening the same file activates its existing confirmation window");
        string other = Path.Combine(temporary, "second.apk");
        await File.WriteAllBytesAsync(other, PackageFixture.Apk(), ct);
        await window.ShowPackageInstallerAsync(other);
        var second = window.InstallerWindow ?? throw new InvalidOperationException("No second installer");
        var secondModel = (PackageInstallerViewModel)second.DataContext!;
        Program.Check(window.InstallerWindows.Count == 2 && secondModel.Name == "Seaglass", "a different launch file opens its own confirmation instead of being dropped");
        second.Close(); await secondModel.DisposeAsync();
        await main.ConnectCommand.Execute().ToTask();
        Program.Check(model.CanInstall && model.SelectedDevice == "模拟手机", "installer tracks the connected device without reopening the package");
        await CaptureAsync(installer, "normal", ct);
        installer.Width = 540; installer.Height = 420;
        await CaptureAsync(installer, "compact", ct);
        installer.Width = 700; installer.Height = 620;
        await CaptureAsync(installer, "normal", ct);
        var install = installer.GetVisualDescendants().OfType<Button>().Single(static b => Equals(b.Content, "安装"));
        var point = install.TranslatePoint(new Point(install.Bounds.Width / 2, install.Bounds.Height / 2), installer)!.Value;
        installer.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
        installer.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
        while (!model.IsInstalled && !model.HasError) await Task.Delay(10, ct);
        Program.Check(model is { IsInstalled: true, CanInstall: false, Status: "安装成功" } && phone.Uploads.Count == 2,
            "actual install button submits every selected split and displays success only after commit");
        installer.Close(); await model.DisposeAsync();
        Program.Check(window.InstallerWindow is null, "closing installer releases its window and package lifetime");
        phone.HoldTerminalOpen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await window.ShowPackageInstallerAsync(path);
        installer = window.InstallerWindow!; model = (PackageInstallerViewModel)installer.DataContext!;
        Task waiting = model.InstallCommand.Execute().ToTask();
        while (!model.IsInstalling) await Task.Delay(10, ct);
        installer.Close(); await model.DisposeAsync(); await waiting;
        Program.Check(!model.IsInstalling && !model.IsInstalled && phone.Uploads.Count == 2, "closing during compatibility query cancels the operation before upload and waits for snapshot release");
        phone.HoldTerminalOpen.SetResult(); phone.HoldTerminalOpen = null;
        await main.RefreshAppsCommand.Execute().ToTask();
        phone.RejectUploads = true;
        await window.ShowPackageInstallerAsync(path);
        installer = window.InstallerWindow!; model = (PackageInstallerViewModel)installer.DataContext!;
        await model.InstallCommand.Execute().ToTask();
        Program.Check(model.HasError && !model.IsInstalled && model.CanInstall, "failed upload is visible and permits retry instead of reporting installed");
        installer.Close(); await model.DisposeAsync();
        await main.DisconnectCommand.Execute().ToTask();
        await File.WriteAllBytesAsync(path, [1, 2, 3], ct);
        await window.ShowPackageInstallerAsync(path);
        installer = window.InstallerWindow!; model = (PackageInstallerViewModel)installer.DataContext!;
        Program.Check(model.HasError && !model.CanInstall && !model.IsLoading, "damaged package remains reviewable with an error and cannot install");
        installer.Close(); await model.DisposeAsync();
    }

    private static async Task CaptureAsync(PackageInstallerWindow window, string suffix, CancellationToken ct)
    {
        window.UpdateLayout();
        await Task.Delay(40, ct);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No installer render");
        string path = Path.Combine(Path.GetTempPath(), "andromeld-installer-" + suffix + ".png");
        frame.Save(path, PngBitmapEncoderOptions.Default);
        Console.WriteLine("Installer screenshot: " + path);
    }
}
