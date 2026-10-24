using System.Runtime.InteropServices;

using Andromeld.Media;
using Andromeld.Platform.Windows;

using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;

using SDL3;

namespace Andromeld.Desktop.Controls;

internal sealed class SdlVideoHost : NativeControlHost
{
    private bool _initialized;
    private DecodedFrame? _latest;
    private NativeInputTransparency? _transparency, _holderTransparency;
    private int _width, _height;
    private nint _window, _renderer, _texture, _display;

    public SdlVideoHost()
    {
        SizeChanged += (_, _) => Dispatcher.UIThread.Post(Draw, DispatcherPriority.Render);
    }

    public bool IsReady => _renderer != 0;
    public event Action<Exception>? Failed;

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        IPlatformHandle handle = base.CreateNativeControlCore(parent);
        try
        {
            if (string.Equals(handle.HandleDescriptor, "XID", StringComparison.Ordinal))
            {
                SDL.SetHint("SDL_VIDEO_DRIVER", "x11");
            }

            if (!SDL.InitSubSystem(SDL.InitFlags.Video))
            {
                throw new IOException(SDL.GetError());
            }

            _initialized = true;
            uint properties = SDL.CreateProperties();
            try
            {
                if (string.Equals(handle.HandleDescriptor, "HWND", StringComparison.Ordinal))
                {
                    SDL.SetPointerProperty(properties, SDL.Props.WindowCreateWin32HWNDPointer, handle.Handle);
                }
                else if (string.Equals(handle.HandleDescriptor, "XID", StringComparison.Ordinal))
                {
                    SDL.SetNumberProperty(properties, SDL.Props.WindowCreateX11WindowNumber, handle.Handle.ToInt64());
                }
                else
                {
                    throw new NotSupportedException("此窗口系统使用 Avalonia 渲染。");
                }

                SDL.SetBooleanProperty(properties, SDL.Props.WindowCreateFocusableBoolean, false);
                _window = SDL.CreateWindowWithProperties(properties);
            }
            finally { SDL.DestroyProperties(properties); }

            if (_window == 0)
            {
                throw new IOException(SDL.GetError());
            }

            _renderer = SDL.CreateRenderer(_window, null!);
            if (_renderer == 0)
            {
                throw new IOException(SDL.GetError());
            }

            // Native child windows must pass input back to Avalonia's hit testing.
            if (string.Equals(handle.HandleDescriptor, "XID", StringComparison.Ordinal))
            {
                _display = XOpenDisplay(0);
                if (_display != 0)
                {
                    XShapeCombineRectangles(_display, handle.Handle, 2, 0, 0, 0, 0, 0, 0);
                    _ = XFlush(_display);
                }
            }
            else if (OperatingSystem.IsWindowsVersionAtLeast(6, 1))
            {
                // Avalonia nests the SDL HWND inside a clipping/attachment HWND. Both must
                // return HTTRANSPARENT, otherwise the holder consumes mouse input before
                // it reaches the top-level Avalonia window and MirrorWindow's handlers.
                _transparency = new NativeInputTransparency(handle.Handle);
                _holderTransparency = new NativeInputTransparency(parent.Handle);
            }

            if (_latest is { } frame)
            {
                Present(frame);
            }
        }
        catch (Exception ex) when (ex is IOException or DllNotFoundException or NotSupportedException or EntryPointNotFoundException)
        {
            Release();
            Failed?.Invoke(ex);
        }

        return handle;
    }

    public unsafe void Present(DecodedFrame frame)
    {
        _latest = frame;
        if (!IsReady)
        {
            return;
        }

        try
        {
            if (_texture == 0 || _width != frame.Width || _height != frame.Height)
            {
                if (_texture != 0)
                {
                    SDL.DestroyTexture(_texture);
                }

                SDL.PixelFormat format = BitConverter.IsLittleEndian ? SDL.PixelFormat.ARGB8888 : SDL.PixelFormat.BGRA8888;
                _texture = SDL.CreateTexture(_renderer, format, SDL.TextureAccess.Streaming, frame.Width, frame.Height);
                if (_texture == 0)
                {
                    throw new IOException(SDL.GetError());
                }

                SDL.SetTextureBlendMode(_texture, SDL.BlendMode.None);
                _width = frame.Width;
                _height = frame.Height;
            }

            fixed (byte* pixels = frame.Bgra)
            {
                if (!SDL.UpdateTexture(_texture, 0, (nint)pixels, frame.Width * 4))
                {
                    throw new IOException(SDL.GetError());
                }
            }

            Draw();
        }
        catch (IOException ex) { Failed?.Invoke(ex); }
    }

    private void Draw()
    {
        if (!IsReady || _texture == 0 || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        SDL.SetWindowSize(_window, Math.Max(1, (int)(Bounds.Width * scaling)), Math.Max(1, (int)(Bounds.Height * scaling)));
        SDL.GetRenderOutputSize(_renderer, out int width, out int height);
        float scale = Math.Min((float)width / _width, (float)height / _height);
        SDL.FRect destination = new()
        {
            X = (width - (_width * scale)) / 2, Y = (height - (_height * scale)) / 2, W = _width * scale, H = _height * scale
        };
        SDL.SetRenderDrawColor(_renderer, 245, 245, 247, 255);
        SDL.RenderClear(_renderer);
        SDL.RenderTexture(_renderer, _texture, 0, destination);
        SDL.RenderPresent(_renderer);
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        Release();
        base.DestroyNativeControlCore(control);
    }

    private void Release()
    {
        if (OperatingSystem.IsWindowsVersionAtLeast(6, 1))
        {
            _transparency?.Dispose();
            _transparency = null;
            _holderTransparency?.Dispose();
            _holderTransparency = null;
        }

        if (_texture != 0)
        {
            SDL.DestroyTexture(_texture);
        }

        if (_renderer != 0)
        {
            SDL.DestroyRenderer(_renderer);
        }

        if (_window != 0)
        {
            SDL.DestroyWindow(_window);
        }

        _texture = _renderer = _window = 0;
        if (_initialized)
        {
            SDL.QuitSubSystem(SDL.InitFlags.Video);
        }

        _initialized = false;
        if (_display != 0)
        {
            _ = XCloseDisplay(_display);
        }

        _display = 0;
        _latest = null;
    }

    [DllImport("libX11.so.6")]
    private static extern nint XOpenDisplay(nint name);

    [DllImport("libX11.so.6")]
    private static extern int XCloseDisplay(nint display);

    [DllImport("libX11.so.6")]
    private static extern int XFlush(nint display);

    [DllImport("libXext.so.6")]
    private static extern void XShapeCombineRectangles(nint display, nint window, int kind, int x, int y, nint rectangles, int count, int operation,
        int ordering);
}