using System.Diagnostics;
using System.IO.Pipes;

using Andromeld.Desktop.Services;

namespace Andromeld.Desktop.Tests;

internal static class SingleInstanceChecks
{
    public static async Task ForwardAsync(string identifier, string[] args)
    {
        using var instance = new DesktopInstance(identifier);
        Program.Check(!instance.IsFirstInstance, "later process finds the existing application instance");
        await instance.ForwardAsync(DesktopInstance.GetPackagePath(args));
    }

    public static async Task RunAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string temporary = Path.Combine(Path.GetTempPath(), "andromeld-instance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        string identifier = "AndroMeld.Test." + Guid.NewGuid().ToString("N");
        try
        {
            using (var first = new DesktopInstance(identifier))
            {
                Program.Check(first.IsFirstInstance, "first process wins application instance election");
                first.StartListening("initial.apk");
                string relative = "中文 package.apks";
                await RunChildAsync(identifier, temporary, [relative], timeout.Token);
                Program.Check(await first.Requests.ReadAsync(timeout.Token) == "initial.apk" && await first.Requests.ReadAsync(timeout.Token) == Path.Combine(temporary, relative),
                    "forwarding is acknowledged before UI startup and relative Unicode paths use the sending process directory");
                if (OperatingSystem.IsLinux())
                {
                    await RunChildAsync(identifier, temporary, ["another terminal.apk"], timeout.Token, newSession: true);
                    Program.Check(await first.Requests.ReadAsync(timeout.Token) == Path.Combine(temporary, "another terminal.apk"), "launching from a separate Linux terminal session forwards to the existing instance");
                }
                await Task.WhenAll(RunChildAsync(identifier, temporary, ["second.apkm"], timeout.Token), RunChildAsync(identifier, temporary, ["third.xapk"], timeout.Token));
                string?[] paths = [await first.Requests.ReadAsync(timeout.Token), await first.Requests.ReadAsync(timeout.Token)];
                Program.Check(paths.Contains(Path.Combine(temporary, "second.apkm")) && paths.Contains(Path.Combine(temporary, "third.xapk")), "simultaneous launches preserve both package requests");
                using (var broken = new NamedPipeClientStream(".", identifier + ".files", PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
                {
                    await broken.ConnectAsync(timeout.Token);
                    await broken.WriteAsync(new byte[] { 255, 255, 255, 255 }, timeout.Token);
                }
                await RunChildAsync(identifier, temporary, [], timeout.Token);
                Program.Check(await first.Requests.ReadAsync(timeout.Token) is null && !first.ListenerCompletion.IsCompleted, "incomplete client does not prevent later activation without a file");
            }
            using var restarted = new DesktopInstance(identifier);
            Program.Check(restarted.IsFirstInstance, "application shutdown releases instance ownership for the next launch");
            Console.WriteLine("Single instance checks passed.");
        }
        finally { Directory.Delete(temporary, true); }
    }

    private static async Task RunChildAsync(string identifier, string workingDirectory, string[] args, CancellationToken ct, bool newSession = false)
    {
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("No process path");
        var start = new ProcessStartInfo(newSession ? "setsid" : executable) { WorkingDirectory = workingDirectory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        if (newSession) { start.ArgumentList.Add("--wait"); start.ArgumentList.Add(executable); }
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("--instance-forward"); start.ArgumentList.Add(identifier);
        foreach (string arg in args) start.ArgumentList.Add(arg);
        using var child = Process.Start(start) ?? throw new IOException("Could not start forwarding process");
        try
        {
            Task<string> output = child.StandardOutput.ReadToEndAsync(ct);
            Task<string> error = child.StandardError.ReadToEndAsync(ct);
            await child.WaitForExitAsync(ct);
            if (child.ExitCode != 0) throw new IOException(await output + await error);
        }
        finally { if (!child.HasExited) child.Kill(entireProcessTree: true); }
    }
}
