using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com.StructuredStorage;
using Windows.Win32.System.Variant;
using Windows.Win32.UI.Shell.PropertiesSystem;

namespace Andromeld.Platform.Windows;

/// <summary>Window-level identity; the owning UI thread sets it before Show and clears it before native destruction.</summary>
[SupportedOSPlatform("windows6.1")]
public static class WindowTaskbarIdentity
{
    public static unsafe void Set(nint window, string? appId)
    {
        if (appId is not null && (appId.Length is 0 or > 128 || appId.Any(char.IsWhiteSpace) || appId.Contains('\0')))
        {
            throw new ArgumentException("AppUserModelID must contain 1–128 characters without whitespace or NUL.", nameof(appId));
        }

        HWND handle = new(window);
        WindowThread.Verify(handle);
        PInvoke.SHGetPropertyStoreForWindow(handle, out IPropertyStore store).ThrowOnFailure();
        PROPVARIANT value = default;
        try
        {
            if (appId is not null)
            {
                value.vt = VARENUM.VT_LPWSTR;
                value.pwszVal = new PWSTR((char*)Marshal.StringToCoTaskMemUni(appId));
            }

            PROPERTYKEY key = PInvoke.PKEY_AppUserModel_ID;
            // The window property store applies SetValue immediately; it does not require Commit.
            store.SetValue(&key, value);
        }
        finally
        {
            _ = PInvoke.PropVariantClear(ref value);
            Marshal.ReleaseComObject(store);
        }
    }

    public static unsafe string? Get(nint window)
    {
        HWND handle = new(window);
        WindowThread.Verify(handle);
        PInvoke.SHGetPropertyStoreForWindow(handle, out IPropertyStore store).ThrowOnFailure();
        PROPVARIANT value = default;
        try
        {
            PROPERTYKEY key = PInvoke.PKEY_AppUserModel_ID;
            store.GetValue(&key, out value);
            return value.vt == VARENUM.VT_LPWSTR ? value.pwszVal.ToString() : null;
        }
        finally
        {
            _ = PInvoke.PropVariantClear(ref value);
            Marshal.ReleaseComObject(store);
        }
    }
}