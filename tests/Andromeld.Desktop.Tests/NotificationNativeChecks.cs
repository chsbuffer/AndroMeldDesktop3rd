using Andromeld.Desktop.Services;
using Andromeld.Desktop.ViewModels;
using Andromeld.Desktop.Views;
using Andromeld.Testing;

using Avalonia;
using Avalonia.Labs.Notifications;
using Avalonia.Threading;

using ReactiveUI.Avalonia;
using ReactiveUI.Primitives;

namespace Andromeld.Desktop.Tests;

internal static class NotificationNativeChecks
{
    internal static void Run()
    {
        using CancellationTokenSource stop = new(TimeSpan.FromSeconds(20));
        Exception? failure = null;
        bool completed = false;
        AppBuilder.Configure<global::Andromeld.Desktop.App>().UseAndromeldPlatform().WithInterFont().UseReactiveUI(static _ => { }).SetupWithoutStarting();
        Dispatcher.UIThread.UnhandledException += (_, e) => { failure = e.Exception; e.Handled = true; stop.Cancel(); };
        Dispatcher.UIThread.Post(async () =>
        {
            string temporary = Path.Combine(Path.GetTempPath(), "andromeld-notification-native-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporary);
            try
            {
                await using MockPhone phone = await MockPhone.StartAsync();
                await using MainViewModel model = new(new Settings { StoragePath = Path.Combine(temporary, "settings.json"), Address = phone.Endpoint.Authority },
                    new StubDiscovery(), usbDiscovery: new StubUsbDiscovery());
                MainWindow window = new() { DataContext = model };
                window.Show();
                await model.ConnectCommand.Execute().ToTask();
                await model.ShowDeviceCommand.Execute().ToTask();
                INativeNotificationManager manager = NativeNotificationManager.Current ?? throw new IOException("Native notification manager is unavailable");
                await phone.PushNotificationsAsync(NotificationChecks.Existing with { Key = "synthetic-native", Title = "AndroMeld 通知同步测试", Text = "合成测试消息 <&> ✓", UpdateTime = 1,
                    Actions = [new() { Index = 9, Title = "回复 <&> '✓'", HasRemoteInput = true }] });
                while (manager.ActiveNotifications.Count == 0) await Task.Delay(20, stop.Token);
                if (manager is LinuxNotificationManager linux)
                {
                    await linux.Pending.WaitAsync(stop.Token);
                    if (linux.LastError is { } nativeError) throw nativeError;
                }
                await Task.Delay(500, stop.Token);
                if (manager is LinuxNotificationManager { LastError: { } updateError }) throw updateError;
                Program.Check(manager.ActiveNotifications.Count == 1, "real native notification backend accepts a synthetic phone notification");
                Program.Check((manager.ActiveNotifications.Values.Single().ReplyActionTag is not null) == OperatingSystem.IsWindows(),
                    "native inline reply is configured only on Windows for a RemoteInput notification");
                uint notificationId = manager.ActiveNotifications.Keys.Single();
                for (int i = 0; i < 12; i++)
                {
                    await phone.PushNotificationsAsync(NotificationChecks.Existing with { Key = "synthetic-native", Title = "AndroMeld 通知同步测试", Text = "合成测试消息 <&> ✓",
                        PostTime = i, UpdateTime = i + 2, Actions = [new() { Index = 9, Title = "回复 <&> '✓'", HasRemoteInput = true }] });
                }

                await model.ShowDeviceCommand.Execute().ToTask();
                await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
                if (manager is LinuxNotificationManager metadata) await metadata.Pending.WaitAsync(stop.Token);
                Program.Check(string.IsNullOrEmpty(model.Error) && manager.ActiveNotifications.ContainsKey(notificationId),
                    "real native backend tolerates repeated metadata-only snapshots without a sync error");
                await phone.PushNotificationsAsync(NotificationChecks.Existing with { Key = "synthetic-native", Title = "AndroMeld 通知更新测试", Text = "合成测试更新", UpdateTime = 2 });
                await Task.Delay(500, stop.Token);
                if (manager is LinuxNotificationManager updating) await updating.Pending.WaitAsync(stop.Token);
                Program.Check(manager.ActiveNotifications.Count == 1 && manager.ActiveNotifications.ContainsKey(notificationId) &&
                    manager.ActiveNotifications[notificationId].ReplyActionTag is null, "real native backend preserves identity and removes withdrawn reply input on update");
                for (int i = 0; i < 12; i++)
                {
                    await phone.PushNotificationsAsync(NotificationChecks.Existing with { Key = "synthetic-native", Title = "AndroMeld 通知更新测试", Text = "合成快速更新 " + i, UpdateTime = i + 3 });
                }

                await model.ShowDeviceCommand.Execute().ToTask();
                await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
                if (manager is LinuxNotificationManager burst) await burst.Pending.WaitAsync(stop.Token);
                Program.Check(string.IsNullOrEmpty(model.Error) && manager.ActiveNotifications[notificationId].Message == "合成快速更新 11" &&
                    manager is not LinuxNotificationManager { LastError: not null }, "real native backend completes a rapid update burst with the latest content and no sync error");
                model.NotificationSyncEnabled = false;
                while (manager.ActiveNotifications.Count != 0) await Task.Delay(20, stop.Token);
                if (manager is LinuxNotificationManager closing)
                {
                    await closing.Pending.WaitAsync(stop.Token);
                    if (closing.LastError is { } closeError) throw closeError;
                }
                Program.Check(true, "real native backend withdraws notifications when synchronization is disabled");
                window.ShowSettings();
                window.UpdateLayout();
                // Render the settings page for visual inspection without capturing the user's desktop.
                using (Avalonia.Media.Imaging.RenderTargetBitmap bitmap = new(new PixelSize((int)window.Width, (int)window.Height)))
                {
                    bitmap.Render(window);
                    bitmap.Save(Path.Combine(Path.GetTempPath(), "andromeld-notification-settings.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                }
                window.DataContext = null;
                window.Close();
            }
            catch (Exception ex) { failure ??= ex; }
            finally
            {
                Directory.Delete(temporary, true);
                completed = true;
                stop.Cancel();
            }
        });
        Dispatcher.UIThread.MainLoop(stop.Token);
        if (failure is not null) throw new InvalidOperationException("Native notification check failed", failure);
        if (!completed) throw new TimeoutException("Native notification check timed out");
    }
}
