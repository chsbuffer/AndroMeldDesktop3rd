using System.Text;

namespace Andromeld.Protocol;

/// <summary>Single-entry name rules used by the original Web file browser.</summary>
public static class FileNameRules
{
    public static bool IsValid(string? name)
    {
        return !string.IsNullOrEmpty(name) && string.Equals(name, name.Trim(), StringComparison.Ordinal) && name is not ("." or "..") &&
               !name.Any(static character => character is '/' or '\\' or < ' ' or '\u007f') &&
               Encoding.UTF8.GetByteCount(name) <= 255;
    }
}
