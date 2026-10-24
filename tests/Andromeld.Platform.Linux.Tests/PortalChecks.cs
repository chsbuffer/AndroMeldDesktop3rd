using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.Versioning;

using Andromeld.Platform.Linux;

using Tmds.DBus.Protocol;

namespace Andromeld.Platform.Linux.Tests;

[SupportedOSPlatform("linux")]
internal static class PortalChecks
{
    public static async Task RunAsync(bool pipeWire, CancellationToken ct)
    {
        var config = System.IO.Path.GetTempFileName();
        await File.WriteAllTextAsync(config, """
            <busconfig>
              <type>session</type><listen>unix:tmpdir=/tmp</listen><auth>EXTERNAL</auth>
              <policy context="default">
                <allow own="*"/><allow send_destination="*"/><allow receive_sender="*"/>
              </policy>
            </busconfig>
            """, ct);
        var info = new ProcessStartInfo("dbus-daemon") { RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("--config-file=" + config); info.ArgumentList.Add("--nofork"); info.ArgumentList.Add("--print-address=1");
        using var bus = Process.Start(info) ?? throw new IOException("Cannot start private D-Bus");
        try
        {
            var address = await bus.StandardOutput.ReadLineAsync(ct) ?? throw new IOException("No private D-Bus address");
            using var server = new DBusConnection(address);
            await server.ConnectAsync();
            var claimed = await server.CallMethodAsync(ClaimName(server), static (message, _) => message.GetBodyReader().ReadUInt32());
            Check(claimed == 1, "mock portal exclusively owns its private D-Bus service");
            var handler = new Portal(server);
            server.AddMethodHandler(handler);
            await using (var session = await ScreenCastPortal.OpenAsync("", ct, address))
            {
                Check(session.NodeId == 43 && session.Serial == 123456789UL && !session.Remote.IsInvalid,
                    "portal negotiates a single source and receives the PipeWire FD and stable serial");
                Check(handler.Calls.SequenceEqual(["CreateSession", "SelectSources", "Start", "OpenPipeWireRemote"]),
                    "portal subscribes before immediate responses and preserves the required request sequence");
                var handle = session.Remote;
                await session.DisposeAsync();
                Check(handle.IsClosed && handler.Calls.Contains("Session.Close"), "stopping closes both the remote FD and portal session");
            }

            handler.Scenario = "cancel";
            try { await ScreenCastPortal.OpenAsync("", ct, address); throw new Exception("Accepted canceled portal"); }
            catch (OperationCanceledException) { Check(true, "user cancellation never opens a PipeWire remote"); }
            handler.Scenario = "hold";
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var pending = ScreenCastPortal.OpenAsync("", cancel.Token, address);
            await handler.StartRequested.Task.WaitAsync(ct);
            cancel.Cancel();
            try { await pending; throw new Exception("Accepted canceled authorization"); }
            catch (OperationCanceledException)
            {
                Check(handler.Calls.Contains("Request.Close") && handler.Calls.Count(c => c == "Session.Close") == 3,
                    "canceling pending authorization closes its request and session before returning");
            }

            if (pipeWire)
            {
                await using var native = await PipeWireServer.StartAsync(address, ct);
                handler.Scenario = "normal";
                handler.NodeId = native.NodeId;
                handler.Serial = native.Serial;
                handler.RemotePath = System.IO.Path.Combine(native.Runtime, "pipewire-0");
                await using var session = await ScreenCastPortal.OpenAsync("", ct, address);
                using var capture = new PipeWireFrameCapture(session.Remote, session.NodeId, session.Serial);
                var pixels = capture.Capture(ct, session.Closed);
                var center = (PipeWireFrameCapture.Height / 2 * PipeWireFrameCapture.Width + PipeWireFrameCapture.Width / 2) * 4;
                Check(pixels[center] > 240 && pixels[center + 1] < 10 && pixels[center + 2] < 10,
                    $"real PipeWire transports synthetic video through the D-Bus FD and production GStreamer source (BGRA {pixels[center]},{pixels[center + 1]},{pixels[center + 2]},{pixels[center + 3]})");
                var above = (300 * PipeWireFrameCapture.Width + PipeWireFrameCapture.Width / 2) * 4;
                Check(pixels[above] < 10 && pixels[above + 1] < 10 && pixels[above + 2] < 10,
                    "PipeWire preserves square pixels and letterboxes the landscape source");
                handler.Revoke();
                await session.Closed.WaitAsync(ct);
                try { capture.Capture(ct, session.Closed); throw new Exception("Accepted a revoked source"); }
                catch (IOException) { Check(true, "desktop revocation stops capture before returning another frame"); }
                capture.Dispose();
                await session.DisposeAsync();

                handler.IncludeSerial = false;
                handler.SourceTypes = 1;
                await using var legacy = await ScreenCastPortal.OpenAsync("", ct, address);
                using var legacyCapture = new PipeWireFrameCapture(legacy.Remote, legacy.NodeId, legacy.Serial);
                var legacyPixels = legacyCapture.Capture(ct, legacy.Closed);
                Check(legacy.Serial is null && legacyPixels[center] > 240 && legacyPixels[center + 1] < 10 && legacyPixels[center + 2] < 10,
                    "older portals without stable serials capture the selected node ID");
            }
        }
        finally
        {
            if (!bus.HasExited) bus.Kill(entireProcessTree: true);
            await bus.WaitForExitAsync(CancellationToken.None);
            File.Delete(config);
        }
    }

    private static MessageBuffer ClaimName(DBusConnection connection)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: "org.freedesktop.DBus", path: "/org/freedesktop/DBus", @interface: "org.freedesktop.DBus", member: "RequestName", signature: "su");
        writer.WriteString("org.freedesktop.portal.Desktop"); writer.WriteUInt32(0);
        return writer.CreateMessage();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS: " + message);
    }

    private sealed class Portal(DBusConnection connection) : IPathMethodHandler
    {
        public string Path => "/org/freedesktop/portal/desktop";
        public bool HandlesChildPaths => true;
        public ConcurrentQueue<string> Calls { get; } = new();
        public string Scenario = "normal";
        public uint NodeId = 43;
        public ulong Serial = 123456789UL;
        public bool IncludeSerial = true;
        public uint SourceTypes = 3;
        public string? RemotePath;
        private string? _sessionPath;
        public TaskCompletionSource StartRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask HandleMethodAsync(MethodContext context)
        {
            var call = context.Request;
            var member = call.MemberAsString ?? "";
            if (call.InterfaceAsString == "org.freedesktop.DBus.Properties" && member == "Get")
            {
                var propertyReader = call.GetBodyReader();
                if (propertyReader.ReadString() == "org.freedesktop.portal.ScreenCast" && propertyReader.ReadString() == "AvailableSourceTypes")
                {
                    using var reply = context.CreateReplyWriter("v");
                    reply.WriteVariantUInt32(SourceTypes); context.Reply(reply.CreateMessage());
                    return default;
                }
            }
            if (call.InterfaceAsString is not ("org.freedesktop.portal.ScreenCast" or "org.freedesktop.portal.Session" or "org.freedesktop.portal.Request"))
            {
                context.ReplyError("org.freedesktop.DBus.Error.UnknownMethod", "Only ScreenCast is implemented by this test portal.");
                return default;
            }
            if (member == "Close")
            {
                Calls.Enqueue(call.InterfaceAsString == "org.freedesktop.portal.Session" ? "Session.Close" : "Request.Close");
                using var reply = context.CreateReplyWriter(null); context.Reply(reply.CreateMessage());
                return default;
            }

            Calls.Enqueue(member);
            if (member == "OpenPipeWireRemote")
            {
                if (RemotePath is { } socketPath)
                {
                    using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    socket.Connect(new UnixDomainSocketEndPoint(socketPath));
                    using var socketReply = context.CreateReplyWriter("h");
                    socketReply.WriteHandle(socket.SafeHandle); context.Reply(socketReply.CreateMessage());
                    return default;
                }
                using var file = File.OpenHandle("/dev/null");
                using var reply = context.CreateReplyWriter("h"); reply.WriteHandle(file); context.Reply(reply.CreateMessage());
                return default;
            }

            var reader = call.GetBodyReader();
            if (member != "CreateSession") reader.ReadObjectPath();
            if (member == "Start") reader.ReadString();
            var options = reader.ReadDictionaryOfStringToVariantValue();
            var sender = call.SenderAsString ?? throw new IOException("No D-Bus caller");
            var id = sender.TrimStart(':').Replace('.', '_');
            var path = $"{Path}/request/{id}/{options["handle_token"].GetString()}";
            if (member == "SelectSources")
                Check(options["types"].GetUInt32() == (SourceTypes & 3u) && !options["multiple"].GetBool(), "source selection requests only supported screens/windows, with a single selection");
            if (member == "Start" && Scenario == "hold") StartRequested.TrySetResult();
            else SendResponse(member, sender, path, id, options);
            using (var reply = context.CreateReplyWriter("o"))
            {
                reply.WriteObjectPath(path); context.Reply(reply.CreateMessage());
            }
            return default;
        }

        private void SendResponse(string member, string sender, string path, string id, Dictionary<string, VariantValue> options)
        {
            using var writer = connection.GetMessageWriter();
            writer.WriteSignalHeader(destination: sender, path: path, @interface: "org.freedesktop.portal.Request", member: "Response", signature: "ua{sv}");
            writer.WriteUInt32(member == "Start" && Scenario == "cancel" ? 1u : 0u);
            var dictionary = writer.WriteDictionaryStart();
            if (member == "CreateSession")
            {
                _sessionPath = $"{Path}/session/{id}/{options["session_handle_token"].GetString()}";
                writer.WriteDictionaryEntryStart(); writer.WriteString("session_handle");
                writer.WriteVariantString(_sessionPath);
            }
            if (member == "Start" && Scenario != "cancel")
            {
                writer.WriteDictionaryEntryStart(); writer.WriteString("streams"); writer.WriteSignature("a(ua{sv})");
                var array = writer.WriteArrayStart(DBusType.Struct);
                writer.WriteStructureStart(); writer.WriteUInt32(NodeId);
                var properties = new Dictionary<string, VariantValue>(StringComparer.Ordinal);
                if (IncludeSerial) properties["pipewire-serial"] = Serial;
                writer.WriteDictionary(properties);
                writer.WriteArrayEnd(array);
            }
            writer.WriteDictionaryEnd(dictionary);
            connection.TrySendMessage(writer.CreateMessage());
        }

        public void Revoke()
        {
            using var writer = connection.GetMessageWriter();
            writer.WriteSignalHeader(path: _sessionPath ?? throw new InvalidOperationException("No mock session"), @interface: "org.freedesktop.portal.Session", member: "Closed", signature: "a{sv}");
            writer.WriteDictionary(new Dictionary<string, VariantValue>(StringComparer.Ordinal));
            connection.TrySendMessage(writer.CreateMessage());
        }
    }
}
