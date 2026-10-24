using System.Security.Cryptography;
using System.Text;

namespace Andromeld.Desktop.Services;

/// <summary>USB serial and wireless-host credentials stay stable when the phone's dynamic ports change.</summary>
internal static class ConnectionKeys
{
    public static string Usb(string serial)
    {
        return "usb:" + Hash(serial);
    }

    public static string Wireless(string host)
    {
        return "wireless:" + Hash(host.ToLowerInvariant());
    }

    public static string WirelessSerial(string serial) => "adb:" + Hash(serial);

    private static string Hash(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
}
