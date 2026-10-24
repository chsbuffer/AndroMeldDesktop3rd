using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;

using Andromeld.Packages;
using Andromeld.Protocol;
using Andromeld.Desktop.Services;

using Avalonia.Media.Imaging;

using ReactiveUI;
using ReactiveUI.Primitives;
using ReactiveUI.SourceGenerators;

namespace Andromeld.Desktop.ViewModels;

/// <summary>UI-owned dialog state. A private disk snapshot stays alive until its active operation exits.</summary>
public sealed partial class PackageInstallerViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly MainViewModel _main;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly IObservable<bool> _canInstall;
    private readonly IDisposable _errors;
    private PackageArchive? _package;
    private Task _operation = Task.CompletedTask;
    private Task? _disposeTask;
    private int _installation;
    private bool _disposed;

    public PackageInstallerViewModel(MainViewModel main)
    {
        _main = main;
        _main.PropertyChanged += DeviceChanged;
        _canInstall = this.WhenAnyValue(static x => x.CanInstall);
        _errors = InstallCommand.ThrownExceptions.Subscribe(ex => Error = ex.Message);
        UpdateDevices();
    }

    public ObservableCollection<PackagePermissionItem> Permissions { get; } = [];
    public ObservableCollection<PackageCertificate> Certificates { get; } = [];
    public ObservableCollection<string> Devices { get; } = [];
    [Reactive] public partial string SelectedDevice { get; set; } = "";
    [Reactive] public partial string Name { get; private set; } = "正在读取安装包…";
    [Reactive] public partial string PackageName { get; private set; } = "";
    [Reactive] public partial string Version { get; private set; } = "";
    [Reactive] public partial string FileName { get; private set; } = "";
    [Reactive] public partial string Size { get; private set; } = "";
    [Reactive] public partial string Format { get; private set; } = "";
    [Reactive] public partial string MinSdk { get; private set; } = "";
    [Reactive] public partial string TargetSdk { get; private set; } = "";
    [Reactive] public partial string Contents { get; private set; } = "";
    [Reactive(nameof(HasIcon))] public partial Bitmap? Icon { get; private set; }
    public bool HasIcon => Icon is not null;
    [Reactive(nameof(CanInstall))] public partial bool IsLoading { get; private set; } = true;
    [Reactive(nameof(CanInstall))] public partial bool IsInstalling { get; private set; }
    [Reactive(nameof(CanInstall))] public partial bool IsInstalled { get; private set; }
    [Reactive(nameof(HasError))] public partial string Error { get; private set; } = "";
    public bool HasError => Error.Length > 0;
    [Reactive] public partial string Status { get; private set; } = "安装前请核对应用信息";
    [Reactive] public partial double Progress { get; private set; }
    public bool CanInstall => !_disposed && !IsLoading && !IsInstalling && !IsInstalled && _package is not null && _main.CanInstallPackages;
    public bool HasPermissions => Permissions.Count > 0;
    public bool HasCertificates => Certificates.Count > 0;

    public Task LoadAsync(string path) => _operation = LoadCoreAsync(path);
    private async Task LoadCoreAsync(string path)
    {
        FileName = Path.GetFileName(path);
        try
        {
            _package = await PackageArchive.OpenAsync(path, _lifetime.Token);
            _lifetime.Token.ThrowIfCancellationRequested();
            var info = _package.Info;
            Name = info.Label; PackageName = info.PackageName;
            Version = string.IsNullOrEmpty(info.VersionName) ? $"版本代码 {info.VersionCode.ToString(CultureInfo.InvariantCulture)}" : $"v{info.VersionName}";
            Size = $"{(_package.Size / 1048576d).ToString("0.##", CultureInfo.CurrentCulture)} MB";
            Format = _package.Format;
            MinSdk = $"API {info.MinSdk.ToString(CultureInfo.InvariantCulture)}";
            TargetSdk = info.TargetSdk is { } target ? $"API {target.ToString(CultureInfo.InvariantCulture)}" : "未声明";
            Contents = $"{_package.Apks.Count} 个 APK" + (_package.Expansions.Count > 0 ? $" · {_package.Expansions.Count} 个 OBB" : "");
            foreach (string permission in _package.Apks.SelectMany(static a => a.Info.Permissions).Distinct(StringComparer.Ordinal))
                Permissions.Add(new(permission));
            foreach (var certificate in info.Certificates) Certificates.Add(certificate);
            try { Icon = PackageIconRenderer.Render(info); }
            catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException or NotSupportedException)
            { /* Unsupported icon encoding falls back to the package icon. */ }
            this.RaisePropertyChanged(nameof(HasPermissions));
            this.RaisePropertyChanged(nameof(HasCertificates));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { /* Closing owns cancellation. */ }
        catch (Exception ex) { Name = "无法读取安装包"; Error = ex.Message; }
        finally { IsLoading = false; }
    }

    [ReactiveCommand(CanExecute = nameof(_canInstall))]
    private Task InstallAsync() => _operation = InstallCoreAsync();

    private async Task InstallCoreAsync()
    {
        if (!CanInstall || _package is not { } package) return;
        IsInstalling = true; Error = ""; Progress = 0;
        int installation = ++_installation;
        try
        {
            Status = "正在检查设备兼容性…";
            var progress = new Progress<PackageInstallProgress>(p =>
            {
                if (_disposed || !IsInstalling || installation != _installation) return;
                Status = p.Message;
                Progress = p.Total > 0 ? p.Sent * 100d / p.Total : 0;
            });
            await _main.InstallPackageAsync(package, progress, _lifetime.Token);
            IsInstalled = true; Progress = 100; Status = "安装成功";
        }
        catch (OperationCanceledException) { Status = "连接中断或已停止等待，请检查手机上的安装状态"; }
        catch (Exception ex) { Error = ex.Message; Status = "安装未完成"; }
        finally { IsInstalling = false; }
    }

    private void DeviceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsConnected) or nameof(MainViewModel.DeviceName)) UpdateDevices();
    }

    private void UpdateDevices()
    {
        Devices.Clear();
        string device = _main.IsConnected ? _main.DeviceName : "请先在主窗口连接设备";
        Devices.Add(device);
        SelectedDevice = device;
        this.RaisePropertyChanged(nameof(CanInstall));
    }

    public ValueTask DisposeAsync() => new(_disposeTask ??= DisposeCoreAsync());

    private async Task DisposeCoreAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _main.PropertyChanged -= DeviceChanged;
        await _lifetime.CancelAsync();
        await _operation;
        Icon?.Dispose();
        _package?.Dispose();
        _errors.Dispose(); InstallCommand.Dispose(); _lifetime.Dispose();
    }
}
