using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.RegularExpressions;

namespace Andromeld.Protocol;

/// <summary>The five URI schemes accepted by the Web service's intentUrl target.</summary>
public sealed partial record AndroidIntentUri
{
    private AndroidIntentUri(string value, string scheme)
    {
        Value = value;
        Scheme = scheme;
    }

    public string Value { get; }
    public string Scheme { get; }

    public static bool TryParse(string? text, [NotNullWhen(true)] out AndroidIntentUri? result)
    {
        result = null;
        string value = text?.Trim() ?? "";
        if (value.Length is 0 or > 8192 || value.Any(static c => c <= 32 || c == 127))
        {
            return false;
        }

        int colon = value.IndexOf(':');
        if (colon > 0 && Uri.CheckSchemeName(value[..colon]))
        {
            string scheme = value[..colon].ToLowerInvariant();
            if (scheme is "http" or "https" or "tel" or "mailto" or "geo")
            {
                if (colon == value.Length - 1 || (scheme is "http" or "https" && !IsWebUrl(value, scheme)))
                {
                    return false;
                }

                result = new AndroidIntentUri(value, scheme);
                return true;
            }
        }

        // Match the reference Web's domain syntax, rather than consulting a changing TLD list.
        if (!BareDomain().IsMatch(value) || !IsWebUrl("https://" + value, "https", true))
        {
            return false;
        }

        result = new AndroidIntentUri("https://" + value, "https");
        return true;
    }

    private static bool IsWebUrl(string value, string scheme, bool positivePort = false)
    {
        // WHATWG accepts missing/extra slashes after an explicit special scheme. Validate that
        // spelling without normalizing the original target sent to Android.
        string authority = value[(value.IndexOf(':') + 1)..].Replace('\\', '/').TrimStart('/');
        if (!Uri.TryCreate(scheme + "://" + authority, UriKind.Absolute, out Uri? uri) || string.IsNullOrEmpty(uri.Host) ||
            (positivePort && uri.Port == 0))
        {
            return false;
        }

        string lastLabel = uri.Host.TrimEnd('.').Split('.')[^1];
        bool numericHost = lastLabel.All(char.IsAsciiDigit) ||
                           (lastLabel.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && lastLabel[2..].All(char.IsAsciiHexDigit));
        return !numericHost || IPAddress.TryParse(uri.Host, out _);
    }

    [GeneratedRegex(@"\A(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+(?:[a-z]{2,63}|xn--[a-z0-9-]{2,59})(?::\d{1,5})?(?:[/?#][^\s]*)?\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex BareDomain();
}