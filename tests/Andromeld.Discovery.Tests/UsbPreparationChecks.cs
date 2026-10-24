using System.Collections.Concurrent;
using Andromeld.Discovery;
using Andromeld.Protocol;
using Andromeld.Testing;

namespace Andromeld.Discovery.Tests;

internal static class UsbPreparationChecks
{
    private static readonly UsbDevice Device = new("USB-ONE", "Test Phone", UsbDeviceState.Online);
    private const string AgentUnavailable = "Result: Bundle[{status=error, errorCode=agent_unavailable}]";
    private const string PrivacyRequired = "Result: Bundle[{status=error, errorCode=privacy_consent_required}]";

    public static async Task RunAsync(Action<bool, string> check)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var ct = stop.Token;
        foreach (var installed in new long?[] { null, 50, 100, 150 })
        {
            var downloads = new FakeUsbDownloads();
            using var http = new HttpClient(downloads);
            await using var adb = new FakeAdbServer { HelperInstalled = installed is not null, HelperVersion = installed };
            var updates = new Recorder();
            await using (await new UsbDiscovery(adb.Endpoint, httpClient: http).OpenAsync(Device, ct, updates)) { }
            var needsInstall = installed is null or < 100;
            var commands = adb.Requests.ToArray();
            check(needsInstall == (adb.InstalledApk is not null) && (!needsInstall || adb.InstalledApk!.SequenceEqual(downloads.Apk)) &&
                adb.TemporaryApks.IsEmpty && adb.ForwardCount == 0 && downloads.Requests.Contains(UsbHelperMetadata.MetadataUri),
                $"helper version {installed?.ToString() ?? "absent"} downloads/verifies/installs only when required and cleans temporary APKs");
            check(needsInstall == commands.Contains("shell:" + UsbHelper.StopAgentCommand) && needsInstall == commands.Contains("shell:" + UsbHelper.StartAgentCommand) &&
                (!needsInstall || Array.IndexOf(commands, "shell:" + UsbHelper.StopAgentCommand) < Array.IndexOf(commands, "shell:" + UsbHelper.StartAgentCommand)),
                $"helper version {installed?.ToString() ?? "absent"} restarts the agent only after package changes");
            if (needsInstall)
                check(updates.Values.Any(p => p.Stage == UsbConnectionStage.Downloading && p.BytesTransferred > 0) &&
                    updates.Values.Any(p => p.Stage == UsbConnectionStage.Copying && p.BytesTransferred == downloads.Apk.Length) &&
                    updates.Values.Any(p => p.Stage == UsbConnectionStage.Installing), "download and ADB Sync publish actual byte progress before installation");
        }

        using (var http = new HttpClient(new FakeUsbDownloads()))
        await using (var unknownVersion = new FakeAdbServer { HelperVersion = null })
        {
            await using (await new UsbDiscovery(unknownVersion.Endpoint, httpClient: http).OpenAsync(Device, ct)) { }
            check(unknownVersion.InstalledApk is not null, "an installed helper without a readable version is upgraded");
        }

        foreach (var installed in new[] { true, false })
        {
            using var http = new HttpClient(new FakeUsbDownloads { Unavailable = true });
            await using var adb = new FakeAdbServer { HelperInstalled = installed };
            var updates = new Recorder();
            if (installed)
            {
                await using (await new UsbDiscovery(adb.Endpoint, httpClient: http).OpenAsync(Device, ct, updates)) { }
                check(updates.Values.Any(p => p.Notice == UsbConnectionNotice.UpdateCheckUnavailable) && adb.InstalledApk is null,
                    "unreachable metadata reuses an installed helper and reports that updates were not checked");
            }
            else
            {
                var error = await FailureAsync(new UsbDiscovery(adb.Endpoint, httpClient: http).OpenAsync(Device, ct));
                check(error.Stage == UsbConnectionStage.CheckingUpdates && error.Message.Contains("联网") && adb.ForwardCount == 0,
                    "unreachable metadata without a helper asks for connectivity and cannot create a forward");
            }
        }

        foreach (var fault in new[] { "metadata", "hash", "install", "version", "stop", "state" })
        {
            var downloads = new FakeUsbDownloads { Metadata = fault == "metadata" ? "{}" : null, CorruptApk = fault == "hash" };
            using var http = new HttpClient(downloads);
            await using var adb = new FakeAdbServer { HelperVersion = 50 };
            if (fault == "install") adb.InstallOutput = "Failure [INSTALL_FAILED_UPDATE_INCOMPATIBLE]\n";
            if (fault == "version") adb.InstalledVersionAfterInstall = 50;
            if (fault == "stop") adb.AgentStopOutput = "agent_restart=failed\n";
            if (fault == "state") adb.AgentState = "{\"pid\":9999}";
            var before = Directory.GetFiles(Path.GetTempPath(), "andromeld-usb-*.apk");
            var error = await FailureAsync(new UsbDiscovery(adb.Endpoint, httpClient: http).OpenAsync(Device, ct));
            check(adb.ForwardCount == 0 && adb.TemporaryApks.IsEmpty && !Directory.GetFiles(Path.GetTempPath(), "andromeld-usb-*.apk").Except(before).Any() &&
                (fault != "hash" || !adb.Requests.Contains("sync:")) && (fault != "stop" || !adb.Requests.Contains("shell:" + UsbHelper.StartAgentCommand)),
                $"{fault} failure stops preparation at {error.Stage}, leaves no forward or temporary APK and cannot silently fall back");
        }

        using (var http = new HttpClient(new FakeUsbDownloads()))
        await using (var adb = new FakeAdbServer { HelperVersion = 50, FailCleanup = true })
        {
            var updates = new Recorder();
            await using (await new UsbDiscovery(adb.Endpoint, httpClient: http).OpenAsync(Device, ct, updates)) { }
            check(updates.Values.Any(p => p.Notice == UsbConnectionNotice.RemoteApkCleanupFailed), "failed remote APK cleanup remains an explicit warning after a successful install");
        }

        using (var http = new HttpClient(new FakeUsbDownloads()))
        await using (var adb = new FakeAdbServer())
        {
            adb.BootstrapOutputs.Enqueue(AgentUnavailable);
            adb.BootstrapOutputs.Enqueue(AgentUnavailable);
            await using (await new UsbDiscovery(adb.Endpoint, httpClient: http).OpenAsync(Device, ct)) { }
            check(adb.Requests.Count(r => r == "shell:" + UsbHelper.StartAgentCommand) == 1 && !adb.Requests.Contains("shell:" + UsbHelper.StopAgentCommand) &&
                adb.Requests.Count(r => r == "shell:" + UsbBootstrap.ShellCommand) == 3, "agent_unavailable starts the agent once, validates state and retries bootstrap without stopping a reused package");
        }
        using (var http = new HttpClient(new FakeUsbDownloads()))
        await using (var adb = new FakeAdbServer { BootstrapOutput = AgentUnavailable })
        {
            await FailureAsync(new UsbDiscovery(adb.Endpoint, httpClient: http).OpenAsync(Device, ct));
            check(adb.Requests.Count(r => r == "shell:" + UsbHelper.StartAgentCommand) == 1 && adb.Requests.Count(r => r == "shell:" + UsbBootstrap.ShellCommand) == 7,
                "agent startup retries at most six bootstrap attempts after the initial failure");
        }
        using (var http = new HttpClient(new FakeUsbDownloads()))
        await using (var adb = new FakeAdbServer())
        {
            adb.BootstrapOutputs.Enqueue(AgentUnavailable);
            adb.BootstrapOutputs.Enqueue(PrivacyRequired);
            adb.BootstrapOutputs.Enqueue(PrivacyRequired);
            var confirmations = 0;
            await using (await new UsbDiscovery(adb.Endpoint, httpClient: http).OpenAsync(Device, ct, confirmPrivacy: token =>
            {
                token.ThrowIfCancellationRequested();
                check(adb.ForwardCount == 0, "privacy confirmation cannot allocate a forward early");
                confirmations++;
                return Task.CompletedTask;
            })) { }
            check(confirmations == 2 && adb.Requests.Count(r => r == "shell:" + UsbHelper.OpenActivityCommand) == 2,
                "privacy required during startup retries opens the phone UI and repeated continuation rechecks consent");
            adb.BootstrapOutput = PrivacyRequired;
            var error = await FailureAsync(new UsbDiscovery(adb.Endpoint, httpClient: http).OpenAsync(Device, ct, confirmPrivacy: _ => Task.FromException(new IOException("synthetic continuation failure"))));
            check(error.Message.Contains("synthetic continuation failure") && adb.ForwardCount == 0, "privacy continuation failures reach the same connection error boundary");
            adb.BootstrapOutput = "Result: Bundle[{status=error, errorCode=web_service_unavailable}]";
            var starts = adb.Requests.Count(r => r == "shell:" + UsbHelper.StartAgentCommand);
            await FailureAsync(new UsbDiscovery(adb.Endpoint, httpClient: http).OpenAsync(Device, ct));
            check(adb.Requests.Count(r => r == "shell:" + UsbHelper.StartAgentCommand) == starts, "non-agent provider errors never start or restart the service");
        }

        foreach (var stage in new[] { UsbConnectionStage.CheckingDevice, UsbConnectionStage.CheckingHelper, UsbConnectionStage.CheckingUpdates,
            UsbConnectionStage.Downloading, UsbConnectionStage.Copying, UsbConnectionStage.Installing, UsbConnectionStage.PreparingService, UsbConnectionStage.WaitingForPrivacyConsent })
        {
            using var http = new HttpClient(new FakeUsbDownloads());
            await using var adb = new FakeAdbServer { HelperInstalled = false, BootstrapOutput = PrivacyRequired };
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var updates = new Recorder(value => { if (value.Stage == stage) cancel.Cancel(); });
            try { await new UsbDiscovery(adb.Endpoint, httpClient: http).OpenAsync(Device, cancel.Token, updates, token => Task.Delay(Timeout.InfiniteTimeSpan, token)); throw new Exception("Expected cancellation"); }
            catch (OperationCanceledException) { }
            check(adb.ForwardCount == 0 && adb.TemporaryApks.IsEmpty &&
                (stage != UsbConnectionStage.Installing || !adb.Requests.Any(r => r.StartsWith("shell:pm install", StringComparison.Ordinal))) &&
                (stage != UsbConnectionStage.PreparingService || !adb.Requests.Contains("shell:" + UsbHelper.StartAgentCommand)),
                $"cancellation at {stage} cleans temporary APKs and starts no subsequent phone operation");
        }

        using (var http = new HttpClient(new FakeUsbDownloads()))
        await using (var adb = new FakeAdbServer { HelperInstalled = false, SyncReplyGate = new(TaskCreationOptions.RunContinuationsAsynchronously) })
        {
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var opening = new UsbDiscovery(adb.Endpoint, httpClient: http).OpenAsync(Device, cancel.Token);
            await adb.SyncRequested.Task.WaitAsync(ct);
            await cancel.CancelAsync();
            try { await opening; throw new Exception("Expected push cancellation"); }
            catch (OperationCanceledException) { }
            adb.SyncReplyGate.SetResult();
            check(adb.TemporaryApks.IsEmpty && !adb.Requests.Any(r => r.StartsWith("shell:pm install", StringComparison.Ordinal)),
                "canceling an in-flight Sync acknowledgement closes the socket, removes the partial APK and never installs it");
        }
        await CheckInFlightCancellationAsync(check, ct);
    }

    private static async Task CheckInFlightCancellationAsync(Action<bool, string> check, CancellationToken ct)
    {
        foreach (var uri in new[] { UsbHelperMetadata.MetadataUri, UsbHelperMetadata.ApkUri })
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var downloads = new FakeUsbDownloads { BeforeResponse = async (request, token) =>
            {
                if (request != uri) return;
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            } };
            using var http = new HttpClient(downloads);
            await using var adb = new FakeAdbServer { HelperInstalled = uri == UsbHelperMetadata.MetadataUri };
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var opening = new UsbDiscovery(adb.Endpoint, httpClient: http).OpenAsync(Device, cancel.Token);
            await entered.Task.WaitAsync(ct);
            await cancel.CancelAsync();
            try { await opening; throw new Exception("Expected download cancellation"); }
            catch (OperationCanceledException) { }
            check(!adb.Requests.Contains("sync:") && !adb.Requests.Contains("shell:" + UsbBootstrap.ShellCommand),
                $"canceling in-flight {uri.AbsolutePath} neither falls back to bootstrap nor copies an APK");
        }

        foreach (var command in new[] { "shell:pm install", "shell:" + UsbHelper.StartAgentCommand })
        {
            using var http = new HttpClient(new FakeUsbDownloads());
            await using var adb = new FakeAdbServer { HelperInstalled = false };
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            adb.BeforeShellReply = request =>
            {
                if (!request.StartsWith(command, StringComparison.Ordinal)) return Task.CompletedTask;
                entered.TrySetResult();
                return release.Task;
            };
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var opening = new UsbDiscovery(adb.Endpoint, httpClient: http).OpenAsync(Device, cancel.Token);
            await entered.Task.WaitAsync(ct);
            await cancel.CancelAsync();
            try { await opening; throw new Exception("Expected shell cancellation"); }
            catch (OperationCanceledException) { }
            release.SetResult();
            check(adb.HelperInstalled && adb.TemporaryApks.IsEmpty && adb.ForwardCount == 0 && !adb.Requests.Contains("shell:" + UsbBootstrap.ShellCommand) &&
                !adb.Requests.Contains("shell:" + UsbHelper.AgentStateCommand), "canceling in-flight install/agent shell leaves completed installation intact and starts no subsequent operation");
        }

        using (var http = new HttpClient(new FakeUsbDownloads()))
        await using (var adb = new FakeAdbServer { HelperInstalled = false, FailCleanup = true })
        {
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var error = await FailureAsync(new UsbDiscovery(adb.Endpoint, httpClient: http).OpenAsync(Device, cancel.Token,
                new Recorder(value => { if (value.Stage == UsbConnectionStage.Installing) cancel.Cancel(); })));
            check(error.Message.Contains("取消") && error.Message.Contains("清理") && adb.ForwardCount == 0, "cleanup failure after cancellation remains actionable instead of being swallowed as ordinary cancellation");
        }
        using (var http = new HttpClient(new FakeUsbDownloads()))
        await using (var adb = new FakeAdbServer { Devices = "USB-ONE\toffline usb:1-2\n" })
        {
            var error = await FailureAsync(new UsbDiscovery(adb.Endpoint, httpClient: http).OpenAsync(Device, ct));
            check(error.Stage == UsbConnectionStage.CheckingDevice && error.Message.Contains("离线") && !adb.Requests.Any(r => r.StartsWith("shell:", StringComparison.Ordinal)),
                "a selected device that has become offline cannot invoke phone setup commands");
        }
        try
        {
            await UsbPreparation.WithTimeoutAsync(async token => { await Task.Delay(Timeout.InfiniteTimeSpan, token); return true; }, 0, ct);
            throw new Exception("Expected timeout");
        }
        catch (IOException ex) { check(ex.Message.Contains("超时"), "per-operation timeout becomes an actionable failure rather than caller cancellation"); }
    }

    private static async Task<UsbConnectionException> FailureAsync(Task<IUsbConnection> task)
    {
        try { await using var connection = await task; throw new Exception("Expected preparation failure"); }
        catch (UsbConnectionException ex) { return ex; }
    }

    private sealed class Recorder(Action<UsbConnectionProgress>? onReport = null) : IProgress<UsbConnectionProgress>
    {
        public ConcurrentQueue<UsbConnectionProgress> Values { get; } = new();
        public void Report(UsbConnectionProgress value) { Values.Enqueue(value); onReport?.Invoke(value); }
    }
}
