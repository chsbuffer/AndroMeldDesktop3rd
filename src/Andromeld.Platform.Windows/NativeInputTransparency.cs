using System.Runtime.Versioning;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;

namespace Andromeld.Platform.Windows;

/// <summary>HWND-thread owned; detaches on native destruction or explicit disposal, whichever happens first.</summary>
[SupportedOSPlatform("windows6.1")]
public sealed class NativeInputTransparency : IDisposable
{
    private readonly SUBCLASSPROC _callback;
    private readonly uint _ownerThread;
    private readonly HWND _window;
    private bool _attached;
    private bool _disposed;

    public NativeInputTransparency(nint window)
    {
        _window = new HWND(window);
        WindowThread.Verify(_window);
        _ownerThread = PInvoke.GetCurrentThreadId();
        _callback = WindowProc;
        if (!PInvoke.SetWindowSubclass(_window, _callback, 1, 0))
        {
            throw new IOException("无法将镜像输入交给 Avalonia。");
        }

        _attached = true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (PInvoke.GetCurrentThreadId() != _ownerThread)
        {
            throw new InvalidOperationException("The calling thread must own the window.");
        }

        if (_attached)
        {
            PInvoke.RemoveWindowSubclass(_window, _callback, 1);
        }

        _attached = false;
        _disposed = true;
    }

    private LRESULT WindowProc(HWND window, uint message, WPARAM wParam, LPARAM lParam, nuint id, nuint data)
    {
        if (message == 0x84)
        {
            return new LRESULT(-1); // WM_NCHITTEST -> HTTRANSPARENT
        }

        if (message == 0x82) // WM_NCDESTROY: the parent can destroy the HWND before Avalonia disposes the host.
        {
            PInvoke.RemoveWindowSubclass(window, _callback, id);
            _attached = false;
        }

        return PInvoke.DefSubclassProc(window, message, wParam, lParam);
    }
}