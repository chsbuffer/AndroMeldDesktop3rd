using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;

namespace Andromeld.Platform.Linux.Tests;

/// <summary>A private daemon and synthetic video node; no hardware monitors are started.</summary>
[SupportedOSPlatform("linux")]
internal sealed class PipeWireServer : IAsyncDisposable
{
    private readonly List<Process> _processes = [];
    private readonly Dictionary<string, string> _environment;
    public string Runtime { get; } = Path.Combine(Path.GetTempPath(), "andromeld-pipewire-" + Guid.NewGuid().ToString("N"));
    public uint NodeId { get; private set; }
    public ulong Serial { get; private set; }

    private PipeWireServer(string busAddress)
    {
        Directory.CreateDirectory(Runtime);
        File.SetUnixFileMode(Runtime, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        _environment = new(StringComparer.Ordinal)
        {
            ["XDG_RUNTIME_DIR"] = Runtime,
            ["PIPEWIRE_RUNTIME_DIR"] = Runtime,
            ["PIPEWIRE_REMOTE"] = Path.Combine(Runtime, "pipewire-0"),
            ["XDG_STATE_HOME"] = Path.Combine(Runtime, "state"),
            ["DBUS_SESSION_BUS_ADDRESS"] = busAddress,
        };
    }

    public static async Task<PipeWireServer> StartAsync(string busAddress, CancellationToken ct)
    {
        var server = new PipeWireServer(busAddress);
        try
        {
            server.Start("pipewire");
            while (!File.Exists(Path.Combine(server.Runtime, "pipewire-0"))) await Task.Delay(25, ct);
            server.Start("wireplumber", "--profile", "policy");
            server.Start("gst-launch-1.0", "-q", "videotestsrc", "is-live=true", "pattern=blue", "!",
                "video/x-raw,format=BGRA,width=320,height=240,framerate=15/1,pixel-aspect-ratio=1/1", "!", "pipewiresink", "mode=provide",
                "stream-properties=props,media.class=Video/Source,node.name=andromeld-synthetic-camera");
            while (true)
            {
                var info = server.Info("pw-dump");
                using var dump = Process.Start(info) ?? throw new IOException("Cannot inspect private PipeWire");
                var data = await dump.StandardOutput.ReadToEndAsync(ct);
                await dump.WaitForExitAsync(ct);
                using var document = JsonDocument.Parse(data);
                foreach (var node in document.RootElement.EnumerateArray())
                {
                    if (!node.TryGetProperty("info", out var properties) || !properties.TryGetProperty("props", out var props) ||
                        !props.TryGetProperty("node.name", out var name) || name.GetString() != "andromeld-synthetic-camera") continue;
                    server.NodeId = node.GetProperty("id").GetUInt32();
                    server.Serial = props.GetProperty("object.serial").GetUInt64();
                    return server;
                }
                await Task.Delay(25, ct);
            }
        }
        catch { await server.DisposeAsync(); throw; }
    }

    private ProcessStartInfo Info(string command, params string[] arguments)
    {
        var info = new ProcessStartInfo(command) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        foreach (var pair in _environment) info.Environment[pair.Key] = pair.Value;
        return info;
    }

    private void Start(string command, params string[] arguments)
    {
        var process = Process.Start(Info(command, arguments)) ?? throw new IOException("Cannot start " + command);
        _processes.Add(process);
        _ = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var process in _processes.AsEnumerable().Reverse())
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            process.Dispose();
        }
        Directory.Delete(Runtime, recursive: true);
    }
}
