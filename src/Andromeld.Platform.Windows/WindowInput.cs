using System.Drawing;
using System.Runtime.Versioning;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Andromeld.Platform.Windows;

/// <summary>Native input diagnostics; callers use the owning UI thread, never a background SDL event loop.</summary>
[SupportedOSPlatform("windows6.1")]
public static class WindowInput
{
    public static bool GetKeyboardState(Span<byte> state)
    {
        if (state.Length != 256)
        {
            throw new ArgumentException("Keyboard state must contain 256 bytes.", nameof(state));
        }

        return PInvoke.GetKeyboardState(state);
    }

    public static bool SetKeyboardState(ReadOnlySpan<byte> state)
    {
        if (state.Length != 256)
        {
            throw new ArgumentException("Keyboard state must contain 256 bytes.", nameof(state));
        }

        return PInvoke.SetKeyboardState(state);
    }

    public static nint GetWindow(nint window, uint command)
    {
        return PInvoke.GetWindow(new HWND(window), (GET_WINDOW_CMD)command);
    }

    public static nint GetCapture()
    {
        return PInvoke.GetCapture();
    }

    public static nint WindowFromPoint(NativePoint point)
    {
        return PInvoke.WindowFromPoint(new Point(point.X, point.Y));
    }

    public static bool ScreenToClient(nint window, ref NativePoint point)
    {
        Point native = new(point.X, point.Y);
        BOOL result = PInvoke.ScreenToClient(new HWND(window), ref native);
        point = new NativePoint(native.X, native.Y);
        return result;
    }

    public static nint SendMessage(nint window, uint message, nuint wParam, nint lParam)
    {
        return PInvoke.SendMessage(new HWND(window), message, new WPARAM(wParam), new LPARAM(lParam)).Value;
    }
}