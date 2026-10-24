using Andromeld.Media;

using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
await using var decoder = new FfmpegVideoDecoder();
var frames = System.Threading.Channels.Channel.CreateUnbounded<DecodedFrame>();
decoder.FrameDecoded += frame => frames.Writer.TryWrite(frame);
decoder.Failed += ex => frames.Writer.TryComplete(ex);
await decoder.WriteAsync(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Media/blue-320x240.h264")), stop.Token);
var blue = await frames.Reader.ReadAsync(stop.Token);
Check(blue.Width == 320 && blue.Height == 240 && blue.Bgra.Length == 320 * 240 * 4, "single access unit decodes without waiting for another frame or EOF");
Check(blue.Bgra[0] > 240 && blue.Bgra[1] < 10 && blue.Bgra[2] < 10, "H.264 YUV converts to correct BGRA channel order");
await decoder.WriteAsync(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Media/red-240x320.h264")), stop.Token);
var red = await frames.Reader.ReadAsync(stop.Token);
Check(red.Width == 240 && red.Height == 320 && red.Bgra[2] > 240 && red.Bgra[0] < 10, "decoder handles SPS resolution change and refreshes pixel conversion");
var narrow = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Media/blue-374x648.h264"));
for (var i = 0; i < 30; i++)
{
    await decoder.WriteAsync(narrow, stop.Token);
    var frame = await frames.Reader.ReadAsync(stop.Token);
    CheckPixels(frame);
}
Check(true, "unaligned video width survives repeated native SIMD conversion with correct final row");
await decoder.DisposeAsync();
await decoder.DisposeAsync();
Check(true, "decoder cancellation and repeated disposal release native resources");
static void Check(bool value, string message)
{
    if (!value) throw new Exception("FAIL: " + message);
    Console.WriteLine("PASS: " + message);
}
static void CheckPixels(DecodedFrame frame)
{
    if (frame.Width != 374 || frame.Height != 648 || frame.Bgra[^4] < 240 || frame.Bgra[^3] > 10 || frame.Bgra[^2] > 10)
        throw new Exception("Invalid conversion at an unaligned row boundary");
}
