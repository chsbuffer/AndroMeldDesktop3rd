using System.Text.Json;
using Andromeld.Cache;
using Andromeld.Desktop.Services;
using Andromeld.Desktop.ViewModels;
using Andromeld.Desktop.Views;
using Andromeld.Testing;
using FluentAvalonia.UI.Controls;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using ReactiveUI.Primitives;

namespace Andromeld.Desktop.Tests;

internal static class WorkspaceChecks
{
    public static async Task BeforeConnectAsync(MainWindow main, StubDiscovery discovery, CancellationToken ct)
    {
        main.UpdateLayout();
        var devices = main.FindControl<DeviceConnectionView>("DevicePage")!;
        var model = (MainViewModel)main.DataContext!;
        var nearby = devices.FindControl<ItemsControl>("NearbyDeviceCards")!;
        var code = devices.FindControl<Border>("CodeConnection")!;
        Program.Check(!devices.FindControl<TextBox>("ConnectionCodeBox")!.IsEnabled &&
            !devices.FindControl<Button>("CodeConnectButton")!.IsEnabled &&
            code.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "暂未支持"),
            "standalone code connection is clearly labelled unsupported with disabled controls");
        var address = model.Address;
        model.Address = "";
        model.Code = "1955-JNBT-WANX";
        Program.Check(!model.CanConnect && !model.CanRePair && model.NearbyDevices[0].ConnectCommand.CanExecute(model.NearbyDevices[0]),
            "a nearby card can connect independently of an empty manual address and the separate code input");
        model.Address = address;

        var original = discovery.Devices.Single();
        var second = new Andromeld.Discovery.LanDevice("synthetic-second", "synthetic-second", "第二台模拟手机",
            [new(System.Net.IPAddress.Parse("192.0.2.8"), 12969, "test", "test")]);
        discovery.SetDevices(original, second);
        main.UpdateLayout();
        var first = model.NearbyDevices[0];
        Program.Check(nearby.GetVisualDescendants().OfType<Button>().Count(b => b.Classes.Contains("nearbyDevice")) == 2,
            "each discovered device is rendered as its own clickable card");
        discovery.SetDevices(original with { Name = "更新的模拟手机" });
        Program.Check(ReferenceEquals(first, model.NearbyDevices.Single()) && first.Name == "更新的模拟手机",
            "discovery updates an existing card and removes departed devices");
        discovery.SetDevices(original);

        // A loopback TCP listener holds the preferred WebSocket handshake until Cancel is pressed.
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            model.Address = $"127.0.0.1:{((System.Net.IPEndPoint)listener.LocalEndpoint).Port}";
            var pending = model.ConnectNearbyCommand.Execute(model.NearbyDevices.Single()).ToTask();
            using var socket = await listener.AcceptTcpClientAsync(ct);
            Program.Check(model.IsConnecting && model.IsResolvingDevice && !model.CanConnect && !model.CanSelectNearbyDevice &&
                !model.NearbyDevices.Single().ConnectCommand.CanExecute(model.NearbyDevices.Single()),
                "a card click owns endpoint resolution and disables competing connections immediately");
            await model.CancelConnectCommand.Execute().ToTask();
            try { await pending; throw new Exception("Expected cancellation"); }
            catch (OperationCanceledException) { }
            Program.Check(!model.IsConnecting && !model.IsResolvingDevice && model.Status == "已取消连接" && model.CanConnect,
                "Cancel interrupts the card's endpoint probe and restores all connection actions");
        }
        finally { listener.Stop(); }
        model.Address = address;
        await model.DismissErrorCommand.Execute().ToTask();
        model.Report(new IOException("synthetic connection error"));
        main.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var error = devices.FindControl<FAInfoBar>("DeviceError")!;
        var close = error.GetVisualDescendants().OfType<Button>().Single(button => button.IsEffectivelyVisible);
        var point = close.TranslatePoint(new Point(close.Bounds.Width / 2, close.Bounds.Height / 2), main)!.Value;
        main.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
        main.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
        Program.Check(!model.HasError && !error.IsOpen, "InfoBar close clears the connection error through the view model command");
        model.Report(new IOException("another synthetic connection error"));
        Program.Check(error.IsOpen && error.IsEffectivelyVisible, "a new connection error reopens the previously dismissed InfoBar");
        await model.DismissErrorCommand.Execute().ToTask();
        await model.ShowSettingsCommand.Execute().ToTask(); main.UpdateLayout();
        Program.Check(main.FindControl<SettingsView>("SettingsPage")!.IsEffectivelyVisible && !devices.IsEffectivelyVisible,
            "page commands isolate device management, applications and settings in one main window");
        await model.ShowDeviceCommand.Execute().ToTask();
    }

    public static async Task ConnectByCardAsync(MainViewModel model, MainWindow main, MockPhone phone, CancellationToken ct)
    {
        var page = main.FindControl<DeviceConnectionView>("DevicePage")!;
        main.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var card = page.FindControl<ItemsControl>("NearbyDeviceCards")!.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("nearbyDevice"));
        var point = card.TranslatePoint(new Point(card.Bounds.Width / 2, card.Bounds.Height / 2), main)!.Value;
        main.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
        main.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
        await UntilAsync(() => model.IsConnected && !model.AppsLoading && model.Apps.Count == 18 &&
            model.FileBrowser.VisibleFiles.Count == 201 && model.HasDeviceStorage, ct);
        Program.Check(phone.AuthenticationTypes.Single() == "pair" && model.Code == "1955-JNBT-WANX",
            "one actual card click resolves and pairs the device without using or clearing the independent connection code");
    }

    public static async Task ConnectedAsync(MainViewModel model, MainWindow main, MockPhone server, string temporary, CancellationToken ct)
    {
        var list = main.FindControl<ListBox>("AppList")!;
        Program.Check(model.IsApplicationsPage && list.IsEffectivelyVisible, "application navigation displays the application page");
        main.Activate(); list.SelectedIndex = 0; list.ContainerFromIndex(0)!.Focus();
        PressAndRelease(main, Key.Right);
        Program.Check(list.SelectedIndex == 1 && model.Mirrors.Count == 0, $"arrow keys navigate tiles without opening a mirror (selected={list.SelectedIndex})");

        var files = main.FileWindow!;
        await model.ShowFilesCommand.Execute().ToTask();
        Program.Check(ReferenceEquals(files, main.FileWindow), "reopening Files activates the existing independent window");
        var navigation = model.FileBrowser.NavigateAsync("/storage/emulated/0/Download");
        files.Close();
        try { await navigation; } catch (OperationCanceledException) { }
        Program.Check(main.FileWindow is null && !model.FileBrowser.FilesLoading && model.IsConnected,
            "closing Files cancels its pending directory operation and preserves the device connection");
        await model.ShowFilesCommand.Execute().ToTask();
        await model.FileBrowser.NavigateAsync("/storage/emulated/0");
        Program.Check(main.FileWindow is not null && !ReferenceEquals(files, main.FileWindow) && model.FileBrowser.VisibleFiles.Count == 201,
            "Files can be reopened and browsed after cancellation");
        // Keep the auxiliary window from intercepting the headless app-pointer checks below.
        main.FileWindow!.Close();

        var pinned = model.Apps.Single(a => a.Package == "test.app5");
        var recent = model.Apps.Single(a => a.Package == "test.app6");
        var otherRecent = model.Apps.Single(a => a.Package == "test.app7");
        await PinWithMenuAsync(pinned, "置顶");
        Program.Check(ReferenceEquals(model.Apps[0], pinned) && pinned.IsPinned && model.Mirrors.Count == 0,
            "right-click Pin puts the application first without launching it");
        await SingleClickAsync(recent);
        Program.Check(model.Mirrors.Count == 1 && ReferenceEquals(model.Apps[0], pinned) && ReferenceEquals(model.Apps[1], recent),
            "one left click opens an application and places it after pinned applications");
        var openedMirror = model.Mirrors.Single();
        using (var snapshot = new MemoryStream(openedMirror.AppIconPng ?? throw new Exception("Missing application window icon.")))
        using (var bitmap = new Avalonia.Media.Imaging.Bitmap(snapshot))
        {
            Program.Check(bitmap.PixelSize == recent.Icon!.PixelSize && main.MirrorWindows.Single().Icon is not null,
                "application mirrors receive the real application's independent PNG icon snapshot");
        }
        var iconSnapshot = openedMirror.AppIconPng!;
        using (var source = new MemoryStream(Convert.FromBase64String(MockPhone.Png)))
            recent.ReplaceIcon(new Avalonia.Media.Imaging.Bitmap(source));
        using (var snapshot = new MemoryStream(iconSnapshot))
        using (var bitmap = new Avalonia.Media.Imaging.Bitmap(snapshot))
            Program.Check(bitmap.PixelSize.Width > 0 && ReferenceEquals(openedMirror.AppIconPng, iconSnapshot),
                "replacing and disposing the list bitmap preserves the open mirror's icon");
        var previousIcon = main.MirrorWindows.Single().Icon;
        await model.RefreshAppsCommand.Execute().ToTask();
        Program.Check(openedMirror.AppIconPng is not null && !ReferenceEquals(openedMirror.AppIconPng, iconSnapshot) &&
            !ReferenceEquals(main.MirrorWindows.Single().Icon, previousIcon),
            "newly fetched or refreshed application icons update an already open mirror window");
        var opened = server.OpenedMirrors.Count;
        await SingleClickAsync(recent);
        Program.Check(server.OpenedMirrors.Count == opened && model.Mirrors.Count == 1,
            "clicking an already open application activates its existing mirror");
        await model.ShowFilesCommand.Execute().ToTask();
        main.FileWindow!.Close();
        Program.Check(model.Mirrors.Count == 1 && model.IsConnected, "closing Files preserves open application mirrors");
        foreach (var mirror in model.Mirrors.ToArray()) await mirror.DisposeAsync();
        await SingleClickAsync(otherRecent);
        Program.Check(ReferenceEquals(model.Apps[1], otherRecent) && ReferenceEquals(model.Apps[2], recent),
            "recent applications are ordered by last successful opening");
        foreach (var mirror in model.Mirrors.ToArray()) await mirror.DisposeAsync();
        model.Search = "test.app5";
        await PinWithMenuAsync(pinned, "取消置顶");
        Program.Check(!pinned.IsPinned && model.Apps.Count == 1 && model.Search == "test.app5",
            "Unpin works from filtered results and keeps the search intact");
        model.Search = "";
        await PinWithMenuAsync(pinned, "置顶");
        var saved = JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path.Combine(temporary, "settings.json")))!;
        var usage = saved.AppUsage[AppCache.DeviceKey("mock", "test-token")];
        Program.Check(usage.Pinned.Contains("0:test.app5") && !usage.Pinned.Contains("10:test.app5") &&
            usage.LastOpened.ContainsKey("0:test.app6") && usage.LastOpened.ContainsKey("0:test.app7") &&
            !usage.LastOpened.ContainsKey("0:test.app5") && saved.Tokens.Values.Contains("test-token"),
            "pin and recency preferences persist by authenticated device and Android user; pinning never records an opening");
        model.SelectedApp = null;
        var profileApp = model.Apps.Single(a => a.App.UserId == 10);
        await PinWithMenuAsync(profileApp, "置顶");
        var profileSettings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path.Combine(temporary, "settings.json")))!;
        var profileUsage = profileSettings.AppUsage[AppCache.DeviceKey("mock", "test-token")];
        Program.Check(profileUsage.Pinned.Contains("10:test.app17") && !profileUsage.Pinned.Contains("0:test.app17") && pinned.IsPinned,
            "pinning a work-profile application records its Android user without changing another application's pin");
        await PinWithMenuAsync(profileApp, "取消置顶");
        await VerifyRestoredPreferencesAsync("mock", true);
        await VerifyRestoredPreferencesAsync("other-device", false);

        async Task VerifyRestoredPreferencesAsync(string deviceId, bool shouldRestore)
        {
            await using var restartedPhone = await MockPhone.StartAsync(deviceId);
            await using var restarted = new MainViewModel(new Settings
            {
                StoragePath = Path.Combine(temporary, deviceId, "settings.json"), Address = restartedPhone.Endpoint.Authority,
                ClientId = saved.ClientId, Tokens = new(saved.Tokens), AppUsage = new(saved.AppUsage),
            }, new StubDiscovery(), usbDiscovery: new StubUsbDiscovery());
            await restarted.ConnectCommand.Execute().ToTask();
            if (shouldRestore)
                Program.Check(restarted.Apps.Take(3).Select(a => a.Package).SequenceEqual(["test.app5", "test.app7", "test.app6"]) && restarted.Apps[0].IsPinned,
                    "a new view model restores serialized pins and recency after the same device changes its Web endpoint");
            else
                Program.Check(restarted.Apps.All(a => !a.IsPinned) && restarted.Apps[0].Package != "test.app7",
                    "a different authenticated device does not inherit another device's pins or recent ordering");
        }

        async Task SingleClickAsync(AppTile tile)
        {
            main.Activate();
            await Task.Delay(30, ct);
            main.UpdateLayout();
            var index = model.Apps.IndexOf(tile);
            var item = (ListBoxItem)list.ContainerFromIndex(index)!;
            list.GetVisualDescendants().OfType<ScrollViewer>().Single().Offset = new Vector(0, item.Bounds.Top);
            main.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            var point = item.TranslatePoint(new Point(item.Bounds.Width / 2, 35), main)!.Value;
            main.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
            main.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
            await UntilAsync(() => model.Mirrors.Any(m => m.App == tile.App && m.CanControl) &&
                model.Apps.Take(2).Contains(tile), ct);
        }

        async Task PinWithMenuAsync(AppTile tile, string expected)
        {
            main.Activate();
            await Task.Delay(30, ct);
            main.UpdateLayout();
            var index = model.Apps.IndexOf(tile);
            var item = (ListBoxItem)list.ContainerFromIndex(index)!;
            list.GetVisualDescendants().OfType<ScrollViewer>().Single().Offset = new Vector(0, item.Bounds.Top);
            main.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            var border = item.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("appTile"));
            var point = border.TranslatePoint(new Point(border.Bounds.Width / 2, 30), main)!.Value;
            main.MouseDown(point, MouseButton.Right, RawInputModifiers.None);
            main.MouseUp(point, MouseButton.Right, RawInputModifiers.None);
            var menu = (FAMenuFlyout)item.ContextFlyout!;
            await UntilAsync(() => menu.IsOpen, ct);
            var command = (FAMenuFlyoutItem)menu.Items[0]!;
            Program.Check(command.Text == expected && command.Command is not null && model.Mirrors.Count == 0,
                $"context menu offers {expected} without opening the application");
            command.Command!.Execute(null);
            menu.Hide();
        }
    }

    public static void Reconnected(MainViewModel model) => Program.Check(model.Apps[0].Package == "test.app5" && model.Apps[0].IsPinned,
        "reconnecting restores persisted pins before cached application results are displayed");

    private static void PressAndRelease(Window window, Key key)
    {
        var physical = key == Key.Right ? PhysicalKey.ArrowRight : Enum.Parse<PhysicalKey>(key.ToString());
        window.KeyPress(key, RawInputModifiers.None, physical, null);
        window.KeyRelease(key, RawInputModifiers.None, physical, null);
    }

    private static async Task UntilAsync(Func<bool> condition, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }
}
