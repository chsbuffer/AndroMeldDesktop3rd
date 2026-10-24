using Andromeld.Platform.Linux;
using Andromeld.Platform.Linux.Tests;

if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Linux required.");
using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
await PortalChecks.RunAsync(args.Contains("--pipewire"), stop.Token);
using (var capture = new PipeWireFrameCapture("videotestsrc is-live=true pattern=blue ! video/x-raw,width=320,height=240"))
{
    var frame = capture.Capture(stop.Token);
    var center = (PipeWireFrameCapture.Height / 2 * PipeWireFrameCapture.Width + PipeWireFrameCapture.Width / 2) * 4;
    Check(frame.Length == 720 * 1280 * 4 && frame[center] > 240 && frame[center + 1] < 10 && frame[center + 2] < 10,
        "GStreamer returns BGRA pixels at the camera canvas size");
    Check(frame[0] < 10 && frame[1] < 10 && frame[2] < 10, "landscape sources are letterboxed without cropping");
    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    try { capture.Capture(cancelled.Token); throw new Exception("Accepted a canceled frame"); }
    catch (OperationCanceledException) { Check(true, "canceled frames are never returned"); }
}
using (var ended = new PipeWireFrameCapture("videotestsrc num-buffers=0"))
{
    try { ended.Capture(stop.Token); throw new Exception("Accepted an ended source"); }
    catch (IOException) { Check(true, "a stopped source reports failure instead of reconnecting elsewhere"); }
}
Console.WriteLine("Linux screen camera checks passed.");

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    Console.WriteLine("PASS: " + message);
}
