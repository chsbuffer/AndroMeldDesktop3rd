using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Andromeld.Platform.Windows;

/// <summary>Independent worker owns all GDI resources. No HWND is accessed by the worker.</summary>
[SupportedOSPlatform("windows10.0.19041")]
public static class DesktopRegionCapture
{
    public static void ExcludeWindow(nint window, bool exclude)
    {
        WindowThread.Verify(new HWND(window));
        if (!PInvoke.SetWindowDisplayAffinity(new HWND(window),
                exclude ? WINDOW_DISPLAY_AFFINITY.WDA_EXCLUDEFROMCAPTURE : WINDOW_DISPLAY_AFFINITY.WDA_NONE))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "无法将镜像窗口排除出屏幕摄像头。");
        }
    }

    public static uint GetWindowAffinity(nint window)
    {
        WindowThread.Verify(new HWND(window));
        if (!PInvoke.GetWindowDisplayAffinity(new HWND(window), out uint affinity))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        return affinity;
    }

    public static unsafe DesktopCaptureFrame Capture(DesktopCaptureRegion region)
    {
        if (region.Width <= 0 || region.Height <= 0 || region.Width > 32768 || region.Height > 32768)
        {
            throw new ArgumentOutOfRangeException(nameof(region));
        }

        // Upright desktop canvas; the camera encoder converts it to the sensor's landscape buffer.
        const int width = 720, height = 1280;
        _ = PInvoke.DwmFlush();
        HDC screen = PInvoke.GetDC();
        if (screen.IsNull)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        HDC memory = PInvoke.CreateCompatibleDC(screen);
        if (memory.IsNull)
        {
            _ = PInvoke.ReleaseDC(default, screen);
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        BITMAPINFO info = new()
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)sizeof(BITMAPINFOHEADER),
                biWidth = width,
                biHeight = -height,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0
            }
        };
        void* pixels;
        using DeleteObjectSafeHandle bitmap = PInvoke.CreateDIBSection(screen, &info, DIB_USAGE.DIB_RGB_COLORS, out pixels, null, 0);
        if (bitmap.IsInvalid)
        {
            _ = PInvoke.DeleteDC(memory);
            _ = PInvoke.ReleaseDC(default, screen);
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        DeleteObjectSafeHandle old = PInvoke.SelectObject(memory, bitmap);
        try
        {
            new Span<byte>(pixels, width * height * 4).Clear();
            // Preserve the complete desktop region; letterbox rather than cropping a code at its edge.
            double scale = Math.Min((double)width / region.Width, (double)height / region.Height);
            int targetWidth = Math.Max(1, (int)Math.Round(region.Width * scale));
            int targetHeight = Math.Max(1, (int)Math.Round(region.Height * scale));
            _ = PInvoke.SetStretchBltMode(memory, STRETCH_BLT_MODE.HALFTONE);
            // CAPTUREBLT can hide/redraw the desktop cursor on every frame. The DWM
            // desktop already contains composed windows; affinity excludes our mirror.
            if (!PInvoke.StretchBlt(memory, (width - targetWidth) / 2, (height - targetHeight) / 2, targetWidth, targetHeight,
                    screen, region.X, region.Y, region.Width, region.Height, ROP_CODE.SRCCOPY))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "无法采集镜像窗口背后的桌面。");
            }

            byte[] bytes = new byte[width * height * 4];
            Marshal.Copy((nint)pixels, bytes, 0, bytes.Length);
            return new DesktopCaptureFrame(width, height, bytes);
        }
        finally
        {
            _ = PInvoke.SelectObject(memory, old);
            _ = PInvoke.DeleteDC(memory);
            _ = PInvoke.ReleaseDC(default, screen);
        }
    }
}