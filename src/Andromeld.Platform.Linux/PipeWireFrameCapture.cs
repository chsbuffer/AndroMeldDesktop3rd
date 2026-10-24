using System.Globalization;
using System.Runtime.InteropServices;

using Microsoft.Win32.SafeHandles;

namespace Andromeld.Platform.Linux;

/// <summary>One consumer owns construction, frame reads and disposal in sequence on workers.
/// GStreamer owns the streaming threads; appsink keeps only the latest frame.</summary>
public sealed partial class PipeWireFrameCapture : IDisposable
{
    public const int Width = 720;
    public const int Height = 1280;
    private const string Output = " ! videorate drop-only=true max-rate=15 ! videoconvert ! videoscale add-borders=true ! video/x-raw,format=BGRA,width=720,height=1280,pixel-aspect-ratio=1/1 ! appsink name=frames max-buffers=1 drop=true sync=false";
    private readonly SafeFileHandle? _remote;
    private nint _pipeline, _sink, _bus;

    public PipeWireFrameCapture(SafeFileHandle remote, uint nodeId, ulong? serial)
    {
        var fd = Duplicate(remote, 1030, 3); // F_DUPFD_CLOEXEC: child processes must not inherit this remote.
        if (fd < 0) throw new IOException("无法复制 PipeWire 连接。", new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()));
        _remote = new SafeFileHandle(fd, true);
        var target = serial is { } value
            ? "target-object=" + value.ToString(CultureInfo.InvariantCulture)
            : "path=" + nodeId.ToString(CultureInfo.InvariantCulture);
        Initialize("pipewiresrc fd=" + fd.ToString(CultureInfo.InvariantCulture) + " " + target + " do-timestamp=true keepalive-time=1000 ! video/x-raw,pixel-aspect-ratio=1/1");
    }

    internal PipeWireFrameCapture(string source)
    {
        Initialize(source);
    }

    private void Initialize(string source)
    {
        try
        {
            Gst.Init(0, 0);
            _pipeline = Gst.ParseLaunch(source + Output, out var error);
            ThrowError(error, "无法创建屏幕采集管线。");
            if (_pipeline == 0) throw new IOException("无法创建屏幕采集管线。");
            _sink = Gst.GetByName(_pipeline, "frames");
            _bus = Gst.GetBus(_pipeline);
            if (_sink == 0 || _bus == 0 || Gst.SetState(_pipeline, 4) == 0)
                throw new IOException("无法启动屏幕采集管线。");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Dispose();
            throw new IOException("屏幕摄像头需要 GStreamer、gst-plugins-base 和 PipeWire 的 GStreamer 插件。", ex);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public byte[] Capture(CancellationToken ct, Task? closed = null)
    {
        ObjectDisposedException.ThrowIf(_pipeline == 0, this);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (closed?.IsCompleted == true) throw new IOException("系统已停止屏幕共享。");
            if (timeout.IsCancellationRequested) throw new IOException("屏幕共享未返回画面，请重新选择共享来源。");
            var sample = Gst.TryPullSample(_sink, 100_000_000);
            if (sample != 0)
            {
                try
                {
                    var buffer = Gst.GetBuffer(sample);
                    const int expected = Width * Height * 4;
                    if (buffer == 0 || Gst.GetBufferSize(buffer) != (nuint)expected)
                        throw new IOException("屏幕共享返回了不支持的像素布局。");
                    var pixels = new byte[expected];
                    if (Gst.Extract(buffer, 0, pixels, (nuint)expected) != (nuint)expected)
                        throw new IOException("屏幕共享图像不完整。");
                    return pixels;
                }
                finally { Gst.MiniObjectUnref(sample); }
            }

            var message = Gst.PopFiltered(_bus, 3); // GST_MESSAGE_EOS | GST_MESSAGE_ERROR
            if (message != 0)
            {
                try
                {
                    // A pipeline error or EOS ends this source. Do not silently reconnect to another node.
                    throw new IOException("系统已停止屏幕共享，或 PipeWire 无法读取所选来源。");
                }
                finally { Gst.MiniObjectUnref(message); }
            }
        }
    }

    private static void ThrowError(nint error, string prefix)
    {
        if (error == 0) return;
        try
        {
            var detail = Marshal.PtrToStructure<GError>(error);
            throw new IOException(prefix + " " + Marshal.PtrToStringUTF8(detail.Message));
        }
        finally { Gst.ErrorFree(error); }
    }

    public void Dispose()
    {
        if (_pipeline != 0) _ = Gst.SetState(_pipeline, 1); // GST_STATE_NULL joins streaming workers.
        if (_sink != 0) Gst.ObjectUnref(_sink);
        if (_bus != 0) Gst.ObjectUnref(_bus);
        if (_pipeline != 0) Gst.ObjectUnref(_pipeline);
        _sink = _bus = _pipeline = 0;
        _remote?.Dispose();
    }

    [LibraryImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static partial int Duplicate(SafeFileHandle fd, int command, int minimum);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct GError
    {
        public readonly uint Domain;
        public readonly int Code;
        public readonly nint Message;
    }

    private static partial class Gst
    {
        private const string Core = "libgstreamer-1.0.so.0";
        [LibraryImport(Core, EntryPoint = "gst_init")] public static partial void Init(nint argc, nint argv);
        [LibraryImport(Core, EntryPoint = "gst_parse_launch", StringMarshalling = StringMarshalling.Utf8)] public static partial nint ParseLaunch(string description, out nint error);
        [LibraryImport(Core, EntryPoint = "gst_bin_get_by_name", StringMarshalling = StringMarshalling.Utf8)] public static partial nint GetByName(nint bin, string name);
        [LibraryImport(Core, EntryPoint = "gst_element_get_bus")] public static partial nint GetBus(nint element);
        [LibraryImport(Core, EntryPoint = "gst_element_set_state")] public static partial int SetState(nint element, int state);
        [LibraryImport(Core, EntryPoint = "gst_bus_pop_filtered")] public static partial nint PopFiltered(nint bus, uint types);
        [LibraryImport(Core, EntryPoint = "gst_sample_get_buffer")] public static partial nint GetBuffer(nint sample);
        [LibraryImport(Core, EntryPoint = "gst_buffer_get_size")] public static partial nuint GetBufferSize(nint buffer);
        [LibraryImport(Core, EntryPoint = "gst_buffer_extract")] public static partial nuint Extract(nint buffer, nuint offset, [Out] byte[] data, nuint size);
        [LibraryImport(Core, EntryPoint = "gst_object_unref")] public static partial void ObjectUnref(nint value);
        [LibraryImport(Core, EntryPoint = "gst_mini_object_unref")] public static partial void MiniObjectUnref(nint value);
        [LibraryImport("libgstapp-1.0.so.0", EntryPoint = "gst_app_sink_try_pull_sample")] public static partial nint TryPullSample(nint sink, ulong timeout);
        [LibraryImport("libglib-2.0.so.0", EntryPoint = "g_error_free")] public static partial void ErrorFree(nint error);
    }
}
