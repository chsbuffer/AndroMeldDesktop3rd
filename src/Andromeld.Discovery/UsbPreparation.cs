using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;

using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.Models;
using AdvancedSharpAdbClient.Receivers;

using Andromeld.Protocol;

namespace Andromeld.Discovery;

/// <summary>One connection attempt owns this state; operations and progress callbacks are sequential.</summary>
internal sealed class UsbPreparation(
    AdbClient client,
    HttpClient http,
    IProgress<UsbConnectionProgress>? progress,
    Func<CancellationToken, Task>? confirmPrivacy)
{
    public UsbConnectionStage Stage { get; private set; } = UsbConnectionStage.CheckingDevice;

    public void Report(UsbConnectionStage stage, long bytes = 0, long? total = null, UsbConnectionNotice? notice = null)
    {
        Stage = stage;
        progress?.Report(new UsbConnectionProgress(stage, bytes, total, notice));
    }

    public async Task<(DeviceData Device, UsbBootstrap Bootstrap)> PrepareAsync(string serial, bool wireless, CancellationToken ct)
    {
        Report(UsbConnectionStage.CheckingDevice);
        IEnumerable<DeviceData> devices = await WithTimeoutAsync(token => client.GetDevicesAsync(token), 15, ct).ConfigureAwait(false);
        DeviceData selected = devices.SingleOrDefault(candidate => string.Equals(candidate.Serial, serial, StringComparison.Ordinal))
                              ?? throw new IOException(wireless
                                  ? "无线调试设备已断开，请重新连接后重试。"
                                  : "USB 设备已断开，请重新插入后查找设备。");
        if (selected.State != DeviceState.Online)
        {
            if (selected.State is DeviceState.Unauthorized or DeviceState.Authorizing)
            {
                throw new IOException(wireless
                    ? "请解锁手机并允许这台电脑进行无线调试，然后重新连接。"
                    : "请解锁手机并允许这台电脑进行 USB 调试，然后重新查找设备。");
            }

            throw new IOException(wireless ? "无线调试设备离线，请检查连接后重试。" : "USB 设备离线，请检查连接后重试。");
        }

        Report(UsbConnectionStage.CheckingHelper);
        (bool Installed, long? Version) installed = await ReadInstallationAsync(selected, ct).ConfigureAwait(false);
        Report(UsbConnectionStage.CheckingUpdates);
        UsbHelperMetadata? metadata = await ReadMetadataAsync(installed.Installed, ct).ConfigureAwait(false);
        bool changed = metadata is not null && (!installed.Installed || installed.Version is null || installed.Version < metadata.VersionCode);
        if (changed && metadata is not null)
        {
            await InstallAsync(selected, metadata, ct).ConfigureAwait(false);
        }

        Report(UsbConnectionStage.PreparingService);
        if (changed)
        {
            await PrepareAgentAsync(selected, true, ct).ConfigureAwait(false);
        }

        bool started = changed;
        int retries = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            Report(UsbConnectionStage.PreparingService);
            try
            {
                string output = await ShellAsync(selected, UsbBootstrap.ShellCommand, ct).ConfigureAwait(false);
                return (selected, UsbBootstrap.Parse(output));
            }
            catch (UsbBootstrapException ex) when (ex.ErrorCode is "privacy_consent_required")
            {
                UsbHelper.CheckActivityStarted(await ShellAsync(selected, UsbHelper.OpenActivityCommand, ct).ConfigureAwait(false));
                Report(UsbConnectionStage.WaitingForPrivacyConsent);
                if (confirmPrivacy is null)
                {
                    throw;
                }

                await confirmPrivacy(ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                // A user's explicit continuation starts a fresh bootstrap check, not a service-start loop.
            }
            catch (UsbBootstrapException ex) when (ex.ErrorCode is "agent_unavailable")
            {
                if (retries++ >= 6)
                {
                    throw;
                }

                if (!started)
                {
                    await PrepareAgentAsync(selected, false, ct).ConfigureAwait(false);
                    started = true;
                }

                if (retries > 1)
                {
                    await Task.Delay(500, ct).ConfigureAwait(false);
                }
            }
        }
    }

    private async Task<(bool Installed, long? Version)> ReadInstallationAsync(DeviceData device, CancellationToken ct)
    {
        if (!UsbHelper.IsInstalled(await ShellAsync(device, UsbHelper.PackagePathCommand, ct).ConfigureAwait(false)))
        {
            return (false, null);
        }

        return (true, UsbHelper.ParseVersion(await ShellAsync(device, UsbHelper.PackageVersionCommand, ct).ConfigureAwait(false)));
    }

    private async Task<UsbHelperMetadata?> ReadMetadataAsync(bool installed, CancellationToken ct)
    {
        string json;
        try
        {
            json = await WithTimeoutAsync(async token =>
            {
                using HttpRequestMessage request = NewDownloadRequest(UsbHelperMetadata.MetadataUri);
                using HttpResponseMessage response = await http.SendAsync(request, token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            }, 15, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            if (!installed)
            {
                throw new IOException("无法从官网检查 USB helper；手机尚未安装，请联网后重试。", ex);
            }

            Report(Stage, notice: UsbConnectionNotice.UpdateCheckUnavailable);
            return null;
        }

        // Invalid metadata must fail even when an existing helper could otherwise be used.
        return UsbHelperMetadata.Parse(json);
    }

    private async Task InstallAsync(DeviceData device, UsbHelperMetadata metadata, CancellationToken ct)
    {
        Guid id = Guid.NewGuid();
        string localPath = Path.Combine(Path.GetTempPath(), $"andromeld-usb-{id:N}.apk");
        bool remoteTouched = false;
        Exception? failure = null;
        try
        {
            Report(UsbConnectionStage.Downloading);
            await DownloadAsync(localPath, metadata, ct).ConfigureAwait(false);
            Report(UsbConnectionStage.Copying);
            ct.ThrowIfCancellationRequested();
            remoteTouched = true;
            await PushAsync(device, id, localPath, ct).ConfigureAwait(false);
            Report(UsbConnectionStage.Installing);
            UsbHelper.CheckInstallResult(await ShellAsync(device, UsbHelper.InstallCommand(id), ct, 120).ConfigureAwait(false));
            (bool Installed, long? Version) installed = await ReadInstallationAsync(device, ct).ConfigureAwait(false);
            if (!installed.Installed || installed.Version is null || installed.Version < metadata.VersionCode)
            {
                throw new IOException("安装后 helper 版本未达到官网要求，请检查手机上的安装结果后重试。");
            }
        }
        catch (Exception ex) { failure = ex; }

        // Preserve the original failure while cleaning with an independent deadline, including cancellation.
        List<UsbConnectionNotice> cleanupFailures = await CleanupApkAsync(device, id, localPath, remoteTouched).ConfigureAwait(false);
        foreach (UsbConnectionNotice notice in cleanupFailures)
        {
            Report(Stage, notice: notice);
        }

        if (cleanupFailures.Count > 0 && (failure is not null || ct.IsCancellationRequested))
        {
            throw new IOException((failure is OperationCanceledException || ct.IsCancellationRequested ? "连接已取消。" : failure?.Message) +
                                  " 临时 APK 未能完全清理，请重新连接手机后检查 /data/local/tmp/AndroMeld-*.apk 和电脑临时目录。", failure);
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private Task<bool> DownloadAsync(string localPath, UsbHelperMetadata metadata, CancellationToken ct)
    {
        return WithTimeoutAsync(async token =>
        {
            using HttpRequestMessage request = NewDownloadRequest(UsbHelperMetadata.ApkUri);
            using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            Stream source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable sourceLifetime = source.ConfigureAwait(false);
            FileStream destination = new(localPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.Asynchronous);
            await using ConfiguredAsyncDisposable destinationLifetime = destination.ConfigureAwait(false);
            byte[] buffer = new byte[65536];
            long received = 0;
            long? total = response.Content.Headers.ContentLength;
            int count;
            while ((count = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                received += count;
                Report(Stage, received, total);
            }

            destination.Position = 0;
            byte[] hash = await SHA256.HashDataAsync(destination, token).ConfigureAwait(false);
            if (received == 0 || !Convert.ToHexString(hash).Equals(metadata.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("USB helper APK 的 SHA-256 校验失败，请稍后重新下载。");
            }

            return true;
        }, 120, ct);
    }

    private Task<bool> PushAsync(DeviceData device, Guid id, string localPath, CancellationToken ct)
    {
        return WithTimeoutAsync(async token =>
        {
            // SyncService's constructor performs synchronous I/O. Run it off the UI thread,
            // connect asynchronously first, and close its socket to interrupt constructor I/O.
            using TcpSocket tcp = new();
            await tcp.ConnectAsync(client.EndPoint, token).ConfigureAwait(false);
            using AdbSocket socket = new(tcp);
            await using ConfiguredAsyncDisposable cancellation = token.Register(socket.Dispose).ConfigureAwait(false);
            try
            {
                await Task.Run(async () =>
                {
                    token.ThrowIfCancellationRequested();
                    using SyncService sync = new(socket, device);
                    FileStream apk = new(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
                    await using ConfiguredAsyncDisposable apkLifetime = apk.ConfigureAwait(false);
                    await sync.PushAsync(apk, UsbHelper.TemporaryApkPath(id), (UnixFileStatus)33188, DateTimeOffset.UtcNow,
                        update => Report(UsbConnectionStage.Copying, (long)update.ReceivedBytesSize, (long)update.TotalBytesToReceive),
                        cancellationToken: token).ConfigureAwait(false);
                }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception) when (token.IsCancellationRequested)
            {
                token.ThrowIfCancellationRequested();
                throw;
            }

            return true;
        }, 120, ct);
    }

    private async Task<List<UsbConnectionNotice>> CleanupApkAsync(DeviceData device, Guid id, string localPath, bool remoteTouched)
    {
        List<UsbConnectionNotice> failures = new();
        if (remoteTouched)
        {
            try
            {
                string result = await ShellAsync(device, UsbHelper.CleanupCommand(id), CancellationToken.None, 3).ConfigureAwait(false);
                if (!result.Split('\n').Any(static line => line.Trim() is "apk_cleanup=ok"))
                {
                    failures.Add(UsbConnectionNotice.RemoteApkCleanupFailed);
                }
            }
            catch (Exception) { failures.Add(UsbConnectionNotice.RemoteApkCleanupFailed); }
        }

        try { File.Delete(localPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failures.Add(UsbConnectionNotice.LocalApkCleanupFailed); }

        return failures;
    }

    private async Task PrepareAgentAsync(DeviceData device, bool restart, CancellationToken ct)
    {
        await WithTimeoutAsync(async token =>
        {
            if (restart)
            {
                UsbHelper.CheckAgentStopped(await ShellAsync(device, UsbHelper.StopAgentCommand, token).ConfigureAwait(false));
            }

            int pid = UsbHelper.ParseAgentPid(await ShellAsync(device, UsbHelper.StartAgentCommand, token).ConfigureAwait(false));
            UsbHelper.CheckAgentState(await ShellAsync(device, UsbHelper.AgentStateCommand, token).ConfigureAwait(false), pid);
            return true;
        }, 20, ct).ConfigureAwait(false);
    }

    private Task<string> ShellAsync(DeviceData device, string command, CancellationToken ct, int seconds = 15)
    {
        return WithTimeoutAsync(async token =>
        {
            ConsoleOutputReceiver output = new();
            await client.ExecuteRemoteCommandAsync(command, device, output, Encoding.UTF8, token).ConfigureAwait(false);
            return output.ToString();
        }, seconds, ct);
    }

    private static HttpRequestMessage NewDownloadRequest(Uri uri)
    {
        return new HttpRequestMessage(HttpMethod.Get, uri) { Headers = { CacheControl = new CacheControlHeaderValue { NoStore = true } } };
    }

    internal static async Task<T> WithTimeoutAsync<T>(Func<CancellationToken, Task<T>> action, int seconds, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        try
        {
            T result = await action(timeout.Token).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested) { throw new IOException("此步骤超时，请检查网络和连接后重试。", ex); }
    }
}