using ReactiveUI.Primitives;
using Andromeld.Testing;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using ReactiveUI.Avalonia;
using Andromeld.Desktop.Services;
using Andromeld.Desktop.ViewModels;
using Andromeld.Desktop.Views;
using Andromeld.Cache;
using Avalonia.Controls;

namespace Andromeld.Desktop.Tests;

public static class UiSmoke
{
    public static void Run(bool live = false, bool mirror = false, bool native = false, bool discovery = false, string? game = null, bool clipboard = false, bool fileDragOnly = false, bool usbOnly = false, bool aboutOnly = false, bool phoneMirrorOnly = false, bool missingFfmpeg = false, bool mirrorInputOnly = false, bool wirelessQrOnly = false, bool clipboardOnly = false, bool screenCameraOnly = false, bool installationOnly = false, bool notificationsOnly = false)
    {
        if (missingFfmpeg && live) throw new ArgumentException("Missing FFmpeg checks use only the mock phone.");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(65));
        Exception? failure = null;
        var completed = false;
        var builder = AppBuilder.Configure<global::Andromeld.Desktop.App>();
        if (native) builder.UseAndromeldPlatform();
        else builder.UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
        builder.UseSkia().UseHarfBuzz().WithInterFont().UseReactiveUI(_ => { }).SetupWithoutStarting();
        Dispatcher.UIThread.Post(async () =>
        {
            var temporary = Path.Combine(Path.GetTempPath(), "andromeld-ui-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporary);
            try
            {
                if (wirelessQrOnly)
                {
                    await WirelessPairingChecks.RunAsync(temporary, stop.Token);
                    Console.WriteLine("Wireless QR pairing desktop checks passed.");
                    return;
                }
                if (usbOnly)
                {
                    await UsbChecks.RunAsync(temporary, stop.Token);
                    Console.WriteLine("USB desktop checks passed.");
                    return;
                }
                await using var phone = live ? null : await MockPhone.StartAsync();
                if (screenCameraOnly)
                {
                    await ScreenCameraChecks.RunAsync(phone ?? throw new InvalidOperationException("Camera checks require a mock phone."), stop.Token);
                    Console.WriteLine("Screen camera desktop checks passed.");
                    return;
                }
                var discoverySource = live ? null : new StubDiscovery();
                var audioPlayers = new List<SharingChecks.Playback>();
                var notifications = notificationsOnly ? new NotificationChecks.Manager() : null;
                if (notificationsOnly) phone!.InitialNotifications = [NotificationChecks.Existing];
                await using var model = live ? new MainViewModel() : new MainViewModel(new Settings { StoragePath = Path.Combine(temporary, "settings.json"), Address = phone!.Endpoint.Authority }, discoverySource,
                    () => { var player = new SharingChecks.Playback(); audioPlayers.Add(player); return player; }, new StubUsbDiscovery(), notificationManager: notifications);
                var window = new MainWindow { DataContext = model };
                window.Show();
                if (installationOnly)
                {
                    await PackageInstallerChecks.RunAsync(model, window, phone!, temporary, stop.Token);
                    window.DataContext = null; window.Close();
                    Console.WriteLine("Package installer desktop checks passed.");
                    return;
                }
                if (notificationsOnly)
                {
                    await model.ConnectCommand.Execute().ToTask();
                    await NotificationChecks.RunAsync(model, window, phone!, notifications!, temporary, stop.Token);
                    window.DataContext = null;
                    window.Close();
                    Console.WriteLine("Notification desktop checks passed.");
                    return;
                }
                if (!live && !native && !phoneMirrorOnly && !fileDragOnly && !mirrorInputOnly && !clipboardOnly) await AboutChecks.RunAsync(model, window, temporary, stop.Token);
                if (aboutOnly)
                {
                    window.DataContext = null; window.Close();
                    return;
                }
                if (!live && !native && !phoneMirrorOnly && !fileDragOnly && !mirrorInputOnly && !clipboardOnly) await WorkspaceChecks.BeforeConnectAsync(window, discoverySource!, stop.Token);
                Program.Check(((System.Windows.Input.ICommand)model.ConnectCommand).CanExecute(null) && !((System.Windows.Input.ICommand)model.RefreshAppsCommand).CanExecute(null), "generated command CanExecute before connecting");
                if (discovery)
                {
                    while (model.NearbyDevices.Count == 0) await Task.Delay(50, stop.Token);
                    var nearby = model.NearbyDevices[0];
                    await model.ConnectNearbyCommand.Execute(nearby).ToTask();
                    Program.Check(model.IsConnected, "real mDNS device card resolves a Web address and connects directly");
                    Console.WriteLine($"真机发现连接：{nearby.Name}");
                }
                else if (!live)
                {
                    Program.Check(model.NearbyDevices.Count == 1, "window starts discovery and binds nearby devices");
                    if (!native && !phoneMirrorOnly && !fileDragOnly && !mirrorInputOnly && !clipboardOnly) await WorkspaceChecks.ConnectByCardAsync(model, window, phone!, stop.Token);
                    else await model.ConnectCommand.Execute().ToTask();
                }
                else await model.ConnectCommand.Execute().ToTask();
                if (!live)
                {
                    var settingsJson = File.ReadAllText(Path.Combine(temporary, "settings.json"));
                    var restored = System.Text.Json.JsonSerializer.Deserialize<Settings>(settingsJson)!;
                    Program.Check(restored.Tokens.Values.Contains("test-token"), "saved pairing credentials survive JSON round trip");
                }
                if (live)
                {
                    Program.Check(model.IsConnected && model.Apps.Count > 0 && model.Apps.Any(a => a.HasIcon), "live UI connects and decodes real application icons");
                    if (!mirror && !discovery && game is null && !clipboard) Program.Check(model.FileBrowser.Roots.Count > 0 && model.FileBrowser.CurrentPath.Length > 0, $"live UI browses real storage root (roots={model.FileBrowser.Roots.Count}, {model.FileBrowser.Error})");
                    Console.WriteLine($"真机 UI：应用 {model.Apps.Count}；有效图标 {model.Apps.Count(a => a.HasIcon)}；目录项 {model.FileBrowser.VisibleFiles.Count}。");
                    if (mirror) await MirrorChecks.RunLiveAsync(model, window, native, stop.Token);
                    if (game is not null) await MirrorChecks.RunLiveGameAsync(model, window, game, native, stop.Token);
                    if (clipboard)
                    {
                        await model.DisconnectCommand.Execute().ToTask();
                        await ClipboardChecks.RunLiveAsync(window, stop.Token);
                    }
                    if (discovery)
                    {
                        await model.DisconnectCommand.Execute().ToTask();
                        await model.ConnectCommand.Execute().ToTask();
                        Program.Check(model.IsConnected && model.Apps.Count > 0 && model.AppsStatus.Contains("复用"), "real reconnect reuses persisted SQLite icons");
                        Console.WriteLine($"真机缓存：{model.AppsStatus}");
                    }
                    window.DataContext = null; window.Close();
                    return;
                }
                if (clipboardOnly)
                {
                    await ClipboardChecks.RunWindowsAsync(model, window, phone!, native, stop.Token);
                    await ClipboardChecks.RunAsync(model, window, phone!, stop.Token, native);
                    window.DataContext = null; window.Close();
                    Console.WriteLine("Clipboard desktop checks passed.");
                    return;
                }
                if (mirrorInputOnly)
                {
                    await MirrorInputMethodChecks.RunAsync(model, window, phone!, stop.Token);
                    await MirrorShortcutChecks.RunAsync(model, window, phone!, Path.Combine(temporary, "settings.json"), stop.Token);
                    window.DataContext = null; window.Close();
                    Console.WriteLine("Mirror input desktop checks passed.");
                    return;
                }
                if (phoneMirrorOnly)
                {
                    await PhoneMirrorChecks.RunAsync(model, window, phone!, stop.Token);
                    window.DataContext = null; window.Close();
                    Console.WriteLine("Phone mirror desktop checks passed.");
                    return;
                }
                Program.Check(model.IsConnected && model.Apps.Count == 18 && model.Apps.All(a => a.HasIcon), "UI connects, decodes PNG icons and displays apps");
                Program.Check(model.FileBrowser.VisibleFiles.Count == 201 && model.FileBrowser.CurrentPath == "/storage/emulated/0", "UI loads initial directory with pagination");
                if (missingFfmpeg)
                {
                    await MirrorChecks.RunWithoutFfmpegAsync(model, window, phone!, temporary, stop.Token);
                    window.DataContext = null; window.Close();
                    return;
                }
                if (fileDragOnly)
                {
                    await FileDragChecks.RunAsync(model, window, phone!, temporary, stop.Token);
                    window.FileWindow?.Close();
                    window.DataContext = null; window.Close();
                    Console.WriteLine("File browser desktop checks passed.");
                    return;
                }
                if (!native) await DeviceInformationChecks.RunAsync(model, window, phone!, stop.Token);
                await model.ShowApplicationsCommand.Execute().ToTask();
                model.Search = "相册";
                window.UpdateLayout();
                ListBoxItem? singleApp = window.FindControl<ListBox>("AppList")!.ContainerFromIndex(0) as ListBoxItem;
                Program.Check(model.Apps.Count == 1 && singleApp is not null && singleApp.Bounds.Width is >= 90 and < 200,
                    "generated Search property updates application filter without stretching a sparse app row");
                model.Search = "";
                await model.ShowFilesCommand.Execute().ToTask();
                Program.Check(window.FileWindow is not null && window.FindControl<ListBox>("AppList")!.IsEffectivelyVisible,
                    "generated file command opens a separate window while applications remain in the main workspace");
                try { await model.FileBrowser.NavigateAsync("/storage/emulated/0/../private"); throw new Exception("Accepted parent traversal"); }
                catch (IOException) { Program.Check(true, "UI confines paths to storage roots"); }
                int fileIndex = model.FileBrowser.VisibleFiles.Select((item, index) => (item, index)).First(pair => !pair.item.IsDir).index;
                model.FileBrowser.Selection.Clear();
                model.FileBrowser.Selection.Select(fileIndex);
                var saved = Path.Combine(temporary, "download.bin");
                await model.FileBrowser.DownloadAsync(model.FileBrowser.VisibleFiles[fileIndex], saved);
                Program.Check(File.ReadAllBytes(saved).SequenceEqual(MockPhone.Payload) && !Directory.EnumerateFiles(temporary, "*.part").Any(), "UI atomically saves complete download");
                if (!native) await WorkspaceChecks.ConnectedAsync(model, window, phone!, temporary, stop.Token);
                if (!native) await SearchChecks.RunAsync(model, window, phone!, stop.Token);
                await FileDragChecks.RunAsync(model, window, phone!, temporary, stop.Token);
                if (!native) await ScreenCameraChecks.RunAsync(phone!, stop.Token);
                await ClipboardChecks.RunAsync(model, window, phone!, stop.Token, native);
                if (!native) await SharingChecks.RunAsync(model, window, phone!, audioPlayers, Path.Combine(temporary, "settings.json"), stop.Token);
                if (!native) await TerminalChecks.RunAsync(model, window, phone!, stop.Token);
                else await TerminalChecks.RunWindowAsync(model, window, phone!, stop.Token);
                if (!native) await MirrorInputMethodChecks.RunAsync(model, window, phone!, stop.Token);
                if (!native) await MirrorShortcutChecks.RunAsync(model, window, phone!, Path.Combine(temporary, "settings.json"), stop.Token);
                if (!native) await PhoneMirrorChecks.RunAsync(model, window, phone!, stop.Token);
                await MirrorChecks.RunMockAsync(model, window, phone!, native, stop.Token);
                if (!native) await TerminalChecks.DisconnectDeviceAsync(model, window, stop.Token);
                await model.DisconnectCommand.Execute().ToTask();
                Program.Check(!model.IsConnected && model.Apps.Count == 0 && !((System.Windows.Input.ICommand)model.RefreshAppsCommand).CanExecute(null), "disconnect clears data and disables generated commands");
                var batches = phone!.IconBatches.Count;
                var showedCache = false;
                model.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(model.AppsStatus) && model.AppsStatus.Contains("本地缓存应用"))
                        showedCache = model.Apps.Count == 18 && model.Apps.All(a => a.HasIcon);
                };
                await model.ConnectCommand.Execute().ToTask();
                Program.Check(showedCache && phone.IconBatches.Count == batches, "reconnect displays persisted app/icon cache before refresh without fetching fresh icons again");
                if (!native) WorkspaceChecks.Reconnected(model);
                await model.RefreshAppsCommand.Execute().ToTask();
                Program.Check(phone.IconBatches.Count == batches + 2, "manual refresh forces icon revalidation");
                var cache = new AppCache(Path.Combine(temporary, "cache-v1.db"));
                var cacheKey = AppCache.DeviceKey("mock", "test-token");
                await cache.SaveIconsAsync(cacheKey, [new("test.app0", 0, [1, 2, 3])], DateTimeOffset.UtcNow, stop.Token);
                await model.DisconnectCommand.Execute().ToTask();
                await model.ConnectCommand.Execute().ToTask();
                Program.Check(phone.IconBatches.Count == batches + 3 && phone.IconBatches[^1] == 1 && model.Apps.All(a => a.HasIcon), "corrupt cached icon is refetched without losing valid cached icons");
                await cache.SaveIconsAsync(cacheKey, [new("test.app0", 0, Convert.FromBase64String(MockPhone.Png))], DateTimeOffset.UtcNow.AddHours(-25), stop.Token);
                await model.DisconnectCommand.Execute().ToTask();
                await model.ConnectCommand.Execute().ToTask();
                Program.Check(phone.IconBatches.Count == batches + 4 && phone.IconBatches[^1] == 1, "expired icon is revalidated on reconnect");
                await model.DisconnectCommand.Execute().ToTask();
                var brokenDirectory = Path.Combine(temporary, "broken");
                Directory.CreateDirectory(brokenDirectory);
                File.WriteAllText(Path.Combine(brokenDirectory, "cache-v1.db"), "not a database");
                await using (var uncached = new MainViewModel(new Settings { StoragePath = Path.Combine(brokenDirectory, "settings.json"), Address = phone.Endpoint.Authority }, new StubDiscovery(), usbDiscovery: new StubUsbDiscovery()))
                {
                    await uncached.ConnectCommand.Execute().ToTask();
                    Program.Check(uncached.IsConnected && uncached.Apps.Count == 18 && uncached.Apps.All(a => a.HasIcon) && uncached.AppsStatus.Contains("缓存不可用"), "unusable database falls back to phone data");
                }
                if (!native) await PairingRecoveryChecks.RunAsync(temporary, stop.Token);
                if (!native) await UsbChecks.RunAsync(temporary, stop.Token);
                window.DataContext = null; window.Close();
                Console.WriteLine("UI smoke checks passed.");
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                try { await DeleteTemporaryAsync(temporary); }
                catch (Exception ex) { failure ??= ex; }
                completed = true;
                stop.Cancel();
            }
        });
        Dispatcher.UIThread.MainLoop(stop.Token);
        if (!completed) throw new TimeoutException("UI checks did not complete before the watchdog expired.");
        if (failure != null) throw new Exception("UI smoke failed", failure);
    }

    private static async Task DeleteTemporaryAsync(string path)
    {
        // A model rejected before app loading can still be finishing its independent SQLite initialization.
        for (var attempt = 0; ; attempt++)
        {
            try { Directory.Delete(path, true); return; }
            catch (IOException) when (attempt < 19) { await Task.Delay(50); }
        }
    }
}
