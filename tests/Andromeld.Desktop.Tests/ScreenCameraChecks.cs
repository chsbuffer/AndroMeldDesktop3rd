using Andromeld.Desktop.Services;
using Andromeld.Desktop.ViewModels;
using Andromeld.Desktop.Views;
using Andromeld.Platform.Windows;
using Andromeld.Protocol;
using Andromeld.Testing;
using SkiaSharp;

namespace Andromeld.Desktop.Tests;

public static class ScreenCameraChecks
{
    public static async Task RunAsync(MockPhone phone, CancellationToken ct)
    {
        await using var client = new AndromeldClient();
        await client.ConnectAsync(phone.Endpoint, "synthetic-camera-client", "test-token", null, ct);
        var app = (await client.GetAppsAsync(ct))[0];
        var preferences = new MirrorDisplayPreferences();
        await using var model = new MirrorViewModel(client, app, ct, displayPreferences: preferences);
        var source = new FakeSource();
        var window = new MirrorWindow(model, cameraSource: source);
        window.Show(); await window.WaitForViewportAsync(); await model.StartAsync();
        var sessionId = model.SessionId!;
        await phone.PushCameraStateAsync(sessionId, true);
        await UntilAsync(() => model.CameraRequested, ct);
        await Task.Delay(150, ct);
        Program.Check(!model.ScreenCameraEnabled && source.Regions.Count == 0 && !source.Excluded,
            "Android camera demand alone never starts desktop capture before the local screen camera switch");
        source.StartPending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        model.ScreenCameraEnabled = true;
        await UntilAsync(() => source.StartCount == 1, ct);
        await Task.Delay(150, ct);
        Program.Check(!model.ScreenCameraRunning && source.Regions.Count == 0,
            "a pending screen sharing authorization never starts capture or marks the camera running");
        model.ScreenCameraEnabled = false;
        await UntilAsync(() => source.StartCancelled, ct);
        source.StartPending.TrySetResult();
        await Task.Delay(100, ct);
        Program.Check(!source.Excluded && !model.ScreenCameraRunning && source.Regions.Count == 0,
            "turning the camera off cancels authorization and ignores its late completion");
        source.StartPending = null;
        model.ScreenCameraEnabled = true;
        await UntilAsync(() => source.Regions.Count > 0 && phone.CameraFrames.Any(f => f.SessionId == sessionId), ct);
        Program.Check(source.Excluded && model.ScreenCameraRunning, "enabling screen camera excludes the mirror and supplies its own application media session");
        var first = source.Regions.Last();
        window.Position = new(320, 240); window.Width = 760;
        await UntilAsync(() => source.Regions.Last() != first, ct);
        Program.Check(source.Regions.Last().X != first.X && source.Regions.Last().Width != first.Width,
            "camera region follows the mirror viewport movement and resize in physical screen coordinates");
        var pending = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Pending = pending;
        await UntilAsync(() => source.InFlight, ct);
        var frames = phone.CameraFrames.Count(f => f.SessionId == sessionId);
        await Task.Delay(200, ct);
        Program.Check(source.MaximumConcurrent == 1, "busy camera capture drops timer ticks instead of queueing or overlapping frames");
        await phone.PushCameraStateAsync(sessionId, false);
        await UntilAsync(() => !model.ScreenCameraRunning && !source.Excluded, ct);
        Program.Check(phone.CameraFrames.Count(f => f.SessionId == sessionId) == frames && !source.InFlight,
            "Android camera off cancels the pending frame and restores window capture after the worker exits");
        source.Pending = null;
        await phone.PushCameraStateAsync(sessionId, true);
        await UntilAsync(() => model.ScreenCameraRunning && source.Regions.Count > 2, ct);
        model.ScreenCameraEnabled = false;
        await UntilAsync(() => !source.Excluded, ct);
        Program.Check(!model.ScreenCameraRunning, "local screen camera switch stops sharing even while Android still requests frames");
        source.Revoked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        model.ScreenCameraEnabled = true;
        await UntilAsync(() => model.ScreenCameraRunning, ct);
        window.WindowState = Avalonia.Controls.WindowState.Minimized;
        source.Revoked.TrySetResult();
        await UntilAsync(() => !model.ScreenCameraRunning && !source.Excluded, ct);
        Program.Check(model.CanControl, "system revocation releases capture even while minimized and leaves mirror control connected");
        source.Revoked = null;
        window.WindowState = Avalonia.Controls.WindowState.Normal;
        model.ScreenCameraEnabled = false;
        source.Failure = new IOException("synthetic capture failure");
        model.ScreenCameraEnabled = true;
        await UntilAsync(() => model.Error.Contains("synthetic capture failure") && !source.Excluded, ct);
        Program.Check(model.CanControl, "desktop capture failure reports an error without stopping the application mirror");
        source.Failure = null; model.ScreenCameraEnabled = false; model.ScreenCameraEnabled = true;
        await UntilAsync(() => source.Excluded, ct);
        var expectedSize = window.ClientSize;
        window.Close();
        await UntilAsync(() => model.IsDisposed && !source.Excluded, ct);
        Program.Check(!model.ScreenCameraRunning, "closing the mirror stops capture and restores exclusion before destroying its HWND");
        await using var reopened = new MirrorViewModel(client, app, ct, displayPreferences: preferences);
        var reopenedSource = new FakeSource();
        var reopenedWindow = new MirrorWindow(reopened, cameraSource: reopenedSource);
        reopenedWindow.Show();
        await reopenedWindow.WaitForViewportAsync();
        await reopened.StartAsync();
        Program.Check(reopened.ScreenCameraEnabled && !reopened.CameraRequested && reopenedSource.Regions.Count == 0,
            "reopening restores the camera choice but still waits for Android demand before capture");
        Program.Check(reopenedWindow.ClientSize == expectedSize,
            "reopening the same application restores its last ordinary window size");
        reopenedWindow.WindowState = Avalonia.Controls.WindowState.FullScreen;
        reopenedWindow.Close();
        await UntilAsync(() => reopened.IsDisposed, ct);
        Program.Check(reopened.WindowSettings is { Maximized: false } saved &&
            saved.Width == expectedSize.Width && saved.Height == expectedSize.Height,
            "closing in fullscreen preserves the ordinary window size instead of saving screen bounds");

        var pixels = Enumerable.Repeat(new byte[] { 255, 0, 0, 0 }, 16 * 9).SelectMany(static p => p).ToArray();
        var jpeg = ScreenCameraImage.Encode(new(16, 9, pixels));
        using var decoded = SKBitmap.Decode(jpeg);
        Program.Check(jpeg[0] == 255 && jpeg[1] == 216 && jpeg.Length <= 1048576 && decoded.Width == 9 && decoded.Height == 16 && decoded.GetPixel(4, 8).Blue > 240,
            "camera encodes a decodable bounded JPEG while treating GDI's zero alpha as opaque");
        CheckImageOrientation();
    }

    private static void CheckImageOrientation()
    {
        using var upright = new SKBitmap(720, 1280, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(upright))
        using (var paint = new SKPaint())
        {
            foreach (var (rect, color) in new[]
            {
                (new SKRect(0, 0, 360, 640), SKColors.Red),
                (new SKRect(360, 0, 720, 640), SKColors.Lime),
                (new SKRect(0, 640, 360, 1280), SKColors.Blue),
                (new SKRect(360, 640, 720, 1280), SKColors.Yellow),
            })
            {
                paint.Color = color;
                canvas.DrawRect(rect, paint);
            }
        }
        var pixels = new byte[upright.ByteCount];
        System.Runtime.InteropServices.Marshal.Copy(upright.GetPixels(), pixels, 0, pixels.Length);
        using var sensor = SKBitmap.Decode(ScreenCameraImage.Encode(new(720, 1280, pixels)));
        Program.Check(sensor.Width == 1280 && sensor.Height == 720,
            "portrait desktop canvas produces the helper's 1280x720 sensor frame without stretching");
        // Sample in upright preview coordinates after Android's clockwise sensor compensation.
        SKColor Preview(int x, int y) => sensor.GetPixel(y, sensor.Height - 1 - x);
        Program.Check(IsNear(Preview(180, 320), SKColors.Red) && IsNear(Preview(540, 320), SKColors.Lime) &&
            IsNear(Preview(180, 960), SKColors.Blue) && IsNear(Preview(540, 960), SKColors.Yellow),
            "Android's 90-degree back-camera preview restores all four asymmetric corners without mirroring or reversing the desktop");
    }

    internal static bool IsNear(SKColor actual, SKColor expected) =>
        Math.Abs(actual.Red - expected.Red) < 15 && Math.Abs(actual.Green - expected.Green) < 15 && Math.Abs(actual.Blue - expected.Blue) < 15;
    private static async Task UntilAsync(Func<bool> predicate, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(20, timeout.Token);
    }
    private sealed class FakeSource : IDesktopCameraSource
    {
        public List<DesktopCaptureRegion> Regions { get; } = [];
        public bool Excluded, InFlight;
        public int MaximumConcurrent;
        private int _concurrent;
        public TaskCompletionSource<byte[]>? Pending;
        public TaskCompletionSource? StartPending;
        public TaskCompletionSource? Revoked;
        public Task WaitForCloseAsync(CancellationToken ct) => Revoked is { } revoked ? revoked.Task.WaitAsync(ct) : Task.Delay(Timeout.Infinite, ct);
        public int StartCount;
        public bool StartCancelled;
        public Exception? Failure;
        public async Task StartAsync(CancellationToken ct)
        {
            StartCount++;
            try
            {
                if (StartPending is { } gate) await gate.Task.WaitAsync(ct);
                ct.ThrowIfCancellationRequested();
                Excluded = true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { StartCancelled = true; throw; }
        }
        public Task StopAsync() { Excluded = false; return Task.CompletedTask; }
        public async Task<byte[]> CaptureAsync(DesktopCaptureRegion region, CancellationToken ct)
        {
            Regions.Add(region); InFlight = true; MaximumConcurrent = Math.Max(MaximumConcurrent, ++_concurrent);
            try
            {
                if (Failure is { } error) throw error;
                return Pending is { } gate ? await gate.Task.WaitAsync(ct) : [255, 216, 255, 217];
            }
            finally { InFlight = false; _concurrent--; }
        }
    }
}
