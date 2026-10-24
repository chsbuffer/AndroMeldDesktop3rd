using Andromeld.Packages;
using Andromeld.Protocol;

namespace Andromeld.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    public bool CanInstallPackages => IsConnected && _client?.SupportsPackageInstallation == true;

    internal async Task InstallPackageAsync(PackageArchive package, IProgress<PackageInstallProgress> progress, CancellationToken ct)
    {
        if (!CanInstallPackages || _client is not { } client) throw new InvalidOperationException("请先连接支持文件传输和终端的设备。");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _connection.Token);
        var target = await client.GetPackageInstallTargetAsync(linked.Token);
        if (_client != client || !IsConnected) throw new OperationCanceledException("设备连接已改变。", linked.Token);
        var apks = package.SelectApks(target.Sdk, target.Abis, target.Density);
        await client.InstallPackageAsync(package.Info.PackageName,
            apks.Select(static f => new PackageInstallFile(f.Size, () => File.OpenRead(f.Path))).ToArray(),
            package.Expansions.Select(static f => new PackageExpansionFile(Path.GetFileName(f.Name), f.Size, () => File.OpenRead(f.Path))).ToArray(),
            progress, linked.Token);
    }
}
