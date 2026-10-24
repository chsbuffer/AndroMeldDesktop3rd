using Andromeld.Desktop.Services;
using Andromeld.Platform.Windows;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using ReactiveUI.Avalonia;
using Andromeld.Desktop.ViewModels;
using Andromeld.Desktop.Views;
using Andromeld.Protocol;
using Andromeld.Testing;
using SkiaSharp;
using System.Runtime.Versioning;
using ReactiveUI.Primitives;

namespace Andromeld.Desktop.Tests;

public static class ScreenCameraNativeChecks
{
    public static void Run()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)) throw new PlatformNotSupportedException("Windows 10 2004 or later required.");
        RunWindows();
    }

    [SupportedOSPlatform("windows10.0.19041")]
    private static void RunWindows()
    {
        AppBuilder.Configure<global::Andromeld.Desktop.App>().UseAndromeldPlatform().UseSkia().UseHarfBuzz().WithInterFont().UseReactiveUI(_ => { }).SetupWithoutStarting();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Exception? failure = null;
        var completed = false;
        Dispatcher.UIThread.Post(async () =>
        {
            var behind = new Window { Width = 900, Height = 700, Position = new(100, 100), Topmost = true, Content = new Border { Background = Brushes.Blue }, Title = "Synthetic camera background" };
            var front = new Window { Width = 320, Height = 240, Position = new(200, 200), Topmost = true, Content = new Border { Background = Brushes.Red }, Title = "Synthetic excluded mirror" };
            try
            {
                behind.Show(); front.Show();
                await Task.Delay(400, stop.Token);
                var handle = front.TryGetPlatformHandle()!;
                var point = front.PointToScreen(new(70, 70));
                var region = new DesktopCaptureRegion(point.X, point.Y, 100, 100);
                var visible = await Task.Run(() => DesktopRegionCapture.Capture(region), stop.Token);
                while (!IsColor(visible, 0, 0, 255))
                {
                    await Task.Delay(100, stop.Token);
                    visible = await Task.Run(() => DesktopRegionCapture.Capture(region), stop.Token);
                }
                Program.Check(IsColor(visible, 0, 0, 255), "native desktop capture initially sees the synthetic foreground window");
                front.Opacity = 0.5;
                await Task.Delay(150, stop.Token);
                var layered = await Task.Run(() => DesktopRegionCapture.Capture(region), stop.Token);
                // Require both windows' colors without assuming the renderer's
                // alpha or color-space details.
                var center = (layered.Height / 2 * layered.Width + layered.Width / 2) * 4;
                Program.Check(layered.Bgra[center] is > 60 and < 240 && layered.Bgra[center + 2] is > 60 and < 240,
                    "SRCCOPY captures the composed translucent window without CAPTUREBLT");
                front.Opacity = 1;
                await Task.Delay(150, stop.Token);
                DesktopRegionCapture.ExcludeWindow(handle.Handle, true);
                Program.Check(DesktopRegionCapture.GetWindowAffinity(handle.Handle) == 0x11, "mirror HWND is excluded from capture on its owning thread");
                await Task.Delay(150, stop.Token);
                var excluded = await Task.Run(() => DesktopRegionCapture.Capture(region), stop.Token);
                Program.Check(IsColor(excluded, 255, 0, 0), "capture sees the desktop window behind the excluded mirror rather than black or recursive video");
                DesktopRegionCapture.ExcludeWindow(handle.Handle, false);
                Program.Check(DesktopRegionCapture.GetWindowAffinity(handle.Handle) == 0, "stopping camera restores normal window capture affinity");
                var restored = await Task.Run(() => DesktopRegionCapture.Capture(region), stop.Token);
                Program.Check(IsColor(restored, 0, 0, 255), "normal desktop capture includes the mirror again after camera exclusion stops");
                front.Close();
                await CheckOrientationAsync(behind, stop.Token);
                behind.Content = new Border { Background = Brushes.Lime };
                await using var phone = await MockPhone.StartAsync();
                await using var client = new AndromeldClient();
                await client.ConnectAsync(phone.Endpoint, "synthetic-native-camera", null, null, stop.Token);
                var app = (await client.GetAppsAsync(stop.Token))[0];
                await using var model = new MirrorViewModel(client, app, stop.Token);
                var mirror = new MirrorWindow(model) { Position = new(220, 220), Width = 400, Height = 480, Topmost = true };
                mirror.Show(); await mirror.WaitForViewportAsync(); await model.StartAsync();
                await UntilAsync(() => model.HasFrame, stop.Token);
                MirrorCaptionChecks.ClickControl(MirrorCaptionChecks.Button(mirror, "TitleDisplayButton"), mirror);
                await UntilAsync(() => mirror.DisplayMenu.IsOpen, stop.Token);
                var cameraItem = mirror.DisplayMenu.Items.OfType<MenuItem>().Single(item => item.Name == "ScreenCameraItem");
                await Task.Delay(100, stop.Token);
                MirrorCaptionChecks.ClickControl(cameraItem, TopLevel.GetTopLevel(cameraItem)!);
                await UntilAsync(() => model.ScreenCameraEnabled && !mirror.DisplayMenu.IsOpen, stop.Token);
                Program.Check(cameraItem.IsChecked, "native screen camera menu click updates its local switch and checkmark");
                await phone.PushCameraStateAsync(model.SessionId!, true);
                await UntilAsync(() => phone.CameraFrames.Any(f => f.SessionId == model.SessionId), stop.Token);
                var jpeg = phone.CameraFrames.Last(f => f.SessionId == model.SessionId).Jpeg;
                using var decoded = SKBitmap.Decode(jpeg);
                var color = decoded.GetPixel(decoded.Width / 2, decoded.Height / 2);
                Program.Check(decoded.Width == 1280 && decoded.Height == 720 && color.Green > 240 && color.Red < 15 && color.Blue < 15,
                    "real Avalonia application mirror streams the synthetic desktop behind it as decodable JPEG on its media socket");
                var selectedFrame = model.Frame;
                var screenshotSelected = false;
                model.SaveScreenshot = frame =>
                {
                    screenshotSelected = ReferenceEquals(frame, selectedFrame);
                    return Task.CompletedTask;
                };
                await model.ScreenshotCommand.Execute().ToTask();
                Program.Check(model.CanScreenshot && screenshotSelected && model.ScreenCameraRunning &&
                    DesktopRegionCapture.GetWindowAffinity(mirror.TryGetPlatformHandle()!.Handle) == 0x11,
                    "application screenshot selects the decoded video frame while camera capture and global window exclusion remain active");
                await phone.PushCameraStateAsync(model.SessionId!, false);
                await UntilAsync(() => !model.ScreenCameraRunning, stop.Token);
                Program.Check(DesktopRegionCapture.GetWindowAffinity(mirror.TryGetPlatformHandle()!.Handle) == 0,
                    "real mirror restores display affinity when Android closes its camera stream");
                await phone.PushCameraStateAsync(model.SessionId!, true);
                await UntilAsync(() => model.ScreenCameraRunning, stop.Token);
                mirror.Close();
                await UntilAsync(() => model.IsDisposed, stop.Token);
                Program.Check(!model.ScreenCameraRunning && !model.HasError, "closing an actively sharing native mirror waits for capture cleanup before destroying the HWND");
            }
            catch (Exception ex) { failure = ex; }
            finally { front.Close(); behind.Close(); completed = true; stop.Cancel(); }
        });
        Dispatcher.UIThread.MainLoop(stop.Token);
        if (!completed) throw new TimeoutException("Native screen camera checks did not finish before the watchdog expired.");
        if (failure is not null) throw new Exception("Native screen camera failed", failure);
    }

    [SupportedOSPlatform("windows10.0.19041")]
    private static async Task CheckOrientationAsync(Window background, CancellationToken ct)
    {
        var pattern = new Grid { RowDefinitions = new("*,*"), ColumnDefinitions = new("*,*") };
        foreach (var (row, column, color) in new[]
        {
            (0, 0, Brushes.Red), (0, 1, Brushes.Lime),
            (1, 0, Brushes.Blue), (1, 1, Brushes.Yellow),
        })
        {
            var corner = new Border { Background = color };
            Grid.SetRow(corner, row); Grid.SetColumn(corner, column);
            pattern.Children.Add(corner);
        }
        background.Content = pattern;
        foreach (var (width, height) in new[] { (360, 640), (720, 440) })
        {
            background.Width = width; background.Height = height;
            await Task.Delay(250, ct);
            var start = pattern.PointToScreen(default);
            var end = pattern.PointToScreen(new(pattern.Bounds.Width, pattern.Bounds.Height));
            var region = new DesktopCaptureRegion(start.X, start.Y, end.X - start.X, end.Y - start.Y);
            var frame = await Task.Run(() => DesktopRegionCapture.Capture(region), ct);
            using var sensor = SKBitmap.Decode(ScreenCameraImage.Encode(frame));
            SKColor Preview(int x, int y) => sensor.GetPixel(y, sensor.Height - 1 - x);
            var scale = Math.Min(720d / region.Width, 1280d / region.Height);
            var contentWidth = (int)Math.Round(region.Width * scale);
            var contentHeight = (int)Math.Round(region.Height * scale);
            var left = (720 - contentWidth) / 2;
            var top = (1280 - contentHeight) / 2;
            SKColor Corner(int x, int y) => Preview(left + contentWidth * x / 4, top + contentHeight * y / 4);
            Program.Check(frame.Width == 720 && frame.Height == 1280 && sensor.Width == 1280 && sensor.Height == 720 &&
                ScreenCameraChecks.IsNear(Corner(1, 1), SKColors.Red) && ScreenCameraChecks.IsNear(Corner(3, 1), SKColors.Lime) &&
                ScreenCameraChecks.IsNear(Corner(1, 3), SKColors.Blue) && ScreenCameraChecks.IsNear(Corner(3, 3), SKColors.Yellow),
                $"{width}x{height} desktop region remains upright and complete after native capture, JPEG and Android sensor compensation");
            var upperEdge = Preview(left + contentWidth * 3 / 4, Math.Min(1279, top + 8));
            var lowerEdge = Preview(left + contentWidth * 3 / 4, Math.Max(0, top + contentHeight - 9));
            Program.Check(upperEdge.Green > 200 && lowerEdge.Red > 200 && lowerEdge.Green > 200 &&
                (top < 16 || ScreenCameraChecks.IsNear(Preview(360, top / 2), SKColors.Black)) &&
                (left < 16 || ScreenCameraChecks.IsNear(Preview(left / 2, 640), SKColors.Black)),
                $"{width}x{height} desktop uses the upright canvas with only aspect-ratio padding and no cropped edges");
        }
    }

    private static bool IsColor(DesktopCaptureFrame frame, byte blue, byte green, byte red)
    {
        var offset = (frame.Height / 2 * frame.Width + frame.Width / 2) * 4;
        Console.WriteLine($"Synthetic center BGRA: {frame.Bgra[offset]},{frame.Bgra[offset + 1]},{frame.Bgra[offset + 2]},{frame.Bgra[offset + 3]}");
        return Math.Abs(frame.Bgra[offset] - blue) < 12 && Math.Abs(frame.Bgra[offset + 1] - green) < 12 && Math.Abs(frame.Bgra[offset + 2] - red) < 12;
    }
    private static async Task UntilAsync(Func<bool> predicate, CancellationToken ct)
    {
        while (!predicate()) await Task.Delay(20, ct);
    }
}
