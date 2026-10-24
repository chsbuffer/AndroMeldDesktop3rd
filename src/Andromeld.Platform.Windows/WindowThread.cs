using System.Runtime.Versioning;

using Windows.Win32;
using Windows.Win32.Foundation;

namespace Andromeld.Platform.Windows;

/// <summary>All window operations must run on the native thread that owns the HWND.</summary>
[SupportedOSPlatform("windows6.1")]
internal static class WindowThread
{
    internal static void Verify(HWND window)
    {
        if (window.IsNull || PInvoke.GetWindowThreadProcessId(window, out _) != PInvoke.GetCurrentThreadId())
        {
            throw new InvalidOperationException("The calling thread must own the window.");
        }
    }
}