using System.Security.Cryptography;

namespace Andromeld.Protocol;

/// <summary>Ephemeral credentials for Android's wireless-debugging QR pairing flow.</summary>
public sealed class AdbPairingCode
{
    private AdbPairingCode(string serviceName, string password)
    {
        ServiceName = serviceName;
        Password = password;
    }

    public string ServiceName { get; }
    public string Password { get; }
    public string QrData => $"WIFI:T:ADB;S:{ServiceName};P:{Password};;";

    public static AdbPairingCode Create()
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        return new AdbPairingCode("studio-" + RandomNumberGenerator.GetString(alphabet, 10), RandomNumberGenerator.GetString(alphabet, 12));
    }

    public static bool IsPairingSuccessful(string response) => response.StartsWith("Successfully paired to ", StringComparison.Ordinal);

    public static bool IsConnectionSuccessful(string response) => response.StartsWith("connected to ", StringComparison.Ordinal) ||
                                                                  response.StartsWith("already connected to ", StringComparison.Ordinal);

    public override string ToString() => ServiceName;
}
