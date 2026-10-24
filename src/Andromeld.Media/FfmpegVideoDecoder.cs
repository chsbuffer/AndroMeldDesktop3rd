using System.Runtime.InteropServices;
using System.Threading.Channels;

using FFmpeg.AutoGen;

namespace Andromeld.Media;

/// <summary>WebSocket messages are complete H.264 access units; no demuxer/probing is needed.</summary>
public sealed class FfmpegVideoDecoder : IAsyncDisposable
{
    private static readonly Lock InitializationLock = new();
    private static bool _initialized;
    private readonly DecoderCore _core;
    private readonly Channel<ReadOnlyMemory<byte>> _packets = Channel.CreateBounded<ReadOnlyMemory<byte>>(
        new BoundedChannelOptions(4) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private bool _disposed;

    public FfmpegVideoDecoder()
    {
        Initialize();
        _core = new DecoderCore();
        _worker = Task.Run(RunAsync, CancellationToken.None); // Always enter the finally that frees native state.
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _packets.Writer.TryComplete();
        await _stop.CancelAsync().ConfigureAwait(false);
        await _worker.ConfigureAwait(false);
        _stop.Dispose();
    }

    public event Action<DecodedFrame>? FrameDecoded;
    public event Action<Exception>? Failed;

    private static void Initialize()
    {
        lock (InitializationLock)
        {
            if (_initialized)
            {
                return;
            }

            string? configured = Environment.GetEnvironmentVariable("ANDROMELD_FFMPEG_LIBS");
            if (!string.IsNullOrEmpty(configured))
            {
                ffmpeg.RootPath = configured;
            }
            else if (OperatingSystem.IsLinux())
            {
                // Pass sonames to dlopen so the system loader honors ldconfig,
                // multiarch paths and LD_LIBRARY_PATH without bundling Linux libraries.
                ffmpeg.RootPath = "";
            }
            else
            {
                string library = OperatingSystem.IsWindows() ? "avcodec-63.dll" : "libavcodec.63.dylib";
                string[] directories = [AppContext.BaseDirectory, Path.Combine(AppContext.BaseDirectory, "runtimes", RuntimeInformation.RuntimeIdentifier, "native")];
                string? directory = Array.Find(directories, path => File.Exists(Path.Combine(path, library)));
                if (directory is not null)
                {
                    ffmpeg.RootPath = directory;
                }
            }

            try
            {
                if (ffmpeg.avcodec_version() >> 16 != 63 || ffmpeg.avutil_version() >> 16 != 61 ||
                    ffmpeg.swscale_version() >> 16 != 10)
                {
                    throw new NotSupportedException("需要 FFmpeg 9（libavcodec 63、libavutil 61、libswscale 10）。");
                }

                ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);
            }
            catch (Exception ex) when (ex is DllNotFoundException or NotSupportedException or EntryPointNotFoundException or BadImageFormatException)
            {
                string hint = OperatingSystem.IsLinux()
                    ? "请通过系统包管理器安装，或用 ANDROMELD_FFMPEG_LIBS 指定目录。"
                    : "请检查程序目录或 runtimes 中对应架构的共享库。";
                throw new IOException("无法加载 FFmpeg 9 共享库（libavcodec 63、libavutil 61、libswscale 10）。" + hint +
                    "设备连接、文件和终端仍可使用。", ex);
            }

            _initialized = true;
        }
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> accessUnit, CancellationToken ct)
    {
        return _packets.Writer.WriteAsync(accessUnit, ct);
    }

    private async Task RunAsync()
    {
        try
        {
            await foreach (ReadOnlyMemory<byte> packet in _packets.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                _core.Decode(packet.Span, frame => FrameDecoded?.Invoke(frame));
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            /* Normal shutdown. */
        }
        catch (Exception ex)
        {
            _packets.Writer.TryComplete(ex);
            if (!_disposed)
            {
                Failed?.Invoke(new IOException("视频解码失败：" + ex.Message, ex));
            }
        }
        finally { _core.Dispose(); }
    }

    private sealed unsafe class DecoderCore : IDisposable
    {
        private AVCodecContext* _context;
        private byte* _conversion;
        private int _conversionSize;
        private AVFrame* _frame;
        private AVPacket* _packet;
        private SwsContext* _scaler;

        public DecoderCore()
        {
            try
            {
                AVCodec* codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_H264);
                if (codec == null)
                {
                    throw new NotSupportedException("FFmpeg 缺少 H.264 解码器。");
                }

                _context = ffmpeg.avcodec_alloc_context3(codec);
                _packet = ffmpeg.av_packet_alloc();
                _frame = ffmpeg.av_frame_alloc();
                if (_context == null || _packet == null || _frame == null)
                {
                    throw new IOException("FFmpeg 无法分配解码缓冲。");
                }

                _context->thread_count = 1;
                _context->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;
                Check(ffmpeg.avcodec_open2(_context, codec, null));
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (_scaler != null)
            {
                ffmpeg.sws_freeContext(_scaler);
                _scaler = null;
            }

            ffmpeg.av_free(_conversion);
            _conversion = null;
            _conversionSize = 0;
            AVFrame* frame = _frame;
            ffmpeg.av_frame_free(&frame);
            _frame = null;
            AVPacket* packet = _packet;
            ffmpeg.av_packet_free(&packet);
            _packet = null;
            AVCodecContext* context = _context;
            ffmpeg.avcodec_free_context(&context);
            _context = null;
        }

        public void Decode(ReadOnlySpan<byte> bytes, Action<DecodedFrame> output)
        {
            Check(ffmpeg.av_new_packet(_packet, bytes.Length));
            try
            {
                bytes.CopyTo(new Span<byte>(_packet->data, bytes.Length));
                int result = ffmpeg.avcodec_send_packet(_context, _packet);
                if (result == ffmpeg.AVERROR(ffmpeg.EAGAIN))
                {
                    Drain(output);
                    result = ffmpeg.avcodec_send_packet(_context, _packet);
                }

                Check(result);
                Drain(output);
            }
            finally { ffmpeg.av_packet_unref(_packet); }
        }

        private void Drain(Action<DecodedFrame> output)
        {
            while (true)
            {
                int result = ffmpeg.avcodec_receive_frame(_context, _frame);
                if (result == ffmpeg.AVERROR(ffmpeg.EAGAIN) || result == ffmpeg.AVERROR_EOF)
                {
                    return;
                }

                Check(result);
                try
                {
                    int width = _frame->width;
                    int height = _frame->height;
                    if (width is <= 0 or > 8192 || height is <= 0 or > 8192 || (long)width * height > 16_777_216)
                    {
                        throw new IOException("视频尺寸超出支持范围。");
                    }

                    _scaler = ffmpeg.sws_getCachedContext(_scaler, width, height, (AVPixelFormat)_frame->format,
                        width, height, AVPixelFormat.AV_PIX_FMT_BGRA, (int)SwsFlags.SWS_FAST_BILINEAR, null, null, null);
                    if (_scaler == null)
                    {
                        throw new IOException("无法转换视频像素格式。");
                    }

                    // SIMD converters require aligned rows and padding; never let them write into the managed heap.
                    int stride = ((width * 4) + 31) & ~31;
                    int required = checked((stride * height) + 64);
                    if (_conversionSize < required)
                    {
                        ffmpeg.av_free(_conversion);
                        _conversion = (byte*)ffmpeg.av_malloc((ulong)required);
                        _conversionSize = _conversion == null ? 0 : required;
                        if (_conversion == null)
                        {
                            throw new IOException("FFmpeg 无法分配像素缓冲。");
                        }
                    }

                    byte_ptrArray4 destination = default;
                    destination[0] = _conversion;
                    int_array4 strides = default;
                    strides[0] = stride;
                    Check(ffmpeg.sws_scale(_scaler, _frame->data, _frame->linesize, 0, height, destination, strides));
                    byte[] pixels = new byte[checked(width * height * 4)];
                    for (int y = 0; y < height; y++)
                    {
                        new ReadOnlySpan<byte>(_conversion + (y * stride), width * 4).CopyTo(pixels.AsSpan(y * width * 4));
                    }

                    output(new DecodedFrame(width, height, pixels));
                }
                finally { ffmpeg.av_frame_unref(_frame); }
            }
        }

        private static void Check(int result)
        {
            if (result >= 0)
            {
                return;
            }

            byte* error = stackalloc byte[256];
            ffmpeg.av_strerror(result, error, 256);
            throw new IOException(Marshal.PtrToStringUTF8((nint)error));
        }
    }
}
