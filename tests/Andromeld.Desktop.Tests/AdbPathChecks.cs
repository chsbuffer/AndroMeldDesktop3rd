using System.Runtime.InteropServices;

using Andromeld.Desktop.Services;

namespace Andromeld.Desktop.Tests;

internal static class AdbPathChecks
{
    public static void Run()
    {
        string temporary = Path.Combine(Path.GetTempPath(), "andromeld adb paths " + Guid.NewGuid().ToString("N"));
        string[] variables = ["ANDROID_HOME", "ANDROID_SDK_ROOT", "PATH"];
        Dictionary<string, string?> original = variables.ToDictionary(static name => name, Environment.GetEnvironmentVariable);
        try
        {
            foreach (string variable in variables) Environment.SetEnvironmentVariable(variable, null);
            Program.Check(DesktopPlatform.FindAdbExecutable(temporary) is null, "missing ADB returns no executable");

            CreateExecutable(Path.Combine(temporary, "runtimes", "unsupported-rid", "native"));
            Program.Check(DesktopPlatform.FindAdbExecutable(temporary) is null, "portable lookup ignores other RID directories");

            string bundled = CreateExecutable(Path.Combine(temporary, "runtimes", RuntimeInformation.RuntimeIdentifier, "native"));
            Program.Check(DesktopPlatform.FindAdbExecutable(temporary) == bundled, "portable runtimes/RID/native ADB works without SDK or PATH configuration");

            string adjacent = CreateExecutable(temporary);
            Program.Check(DesktopPlatform.FindAdbExecutable(temporary) == adjacent, "ADB beside the application is recognized");

            string systemDirectory = Path.Combine(temporary, "system tools");
            string system = CreateExecutable(systemDirectory);
            Environment.SetEnvironmentVariable("PATH", '"' + systemDirectory + '"');
            Program.Check(DesktopPlatform.FindAdbExecutable(temporary) == system, "system PATH ADB takes priority over all application copies");

            string sdk = Path.Combine(temporary, "sdk");
            string sdkAdb = CreateExecutable(Path.Combine(sdk, "platform-tools"));
            Environment.SetEnvironmentVariable("ANDROID_SDK_ROOT", sdk);
            Program.Check(DesktopPlatform.FindAdbExecutable(temporary) == sdkAdb, "Android SDK ADB takes priority over PATH and bundled copies");

            string home = Path.Combine(temporary, "android home");
            string homeAdb = CreateExecutable(Path.Combine(home, "platform-tools"));
            Environment.SetEnvironmentVariable("ANDROID_HOME", home);
            Program.Check(DesktopPlatform.FindAdbExecutable(temporary) == homeAdb, "ANDROID_HOME retains priority over ANDROID_SDK_ROOT");
        }
        finally
        {
            foreach ((string name, string? value) in original) Environment.SetEnvironmentVariable(name, value);
            if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true);
        }
    }

    private static string CreateExecutable(string directory)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, OperatingSystem.IsWindows() ? "adb.exe" : "adb");
        File.WriteAllText(path, "synthetic ADB path fixture; never executed");
        return path;
    }
}
