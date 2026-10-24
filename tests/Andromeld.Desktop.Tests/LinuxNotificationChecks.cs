using System.Diagnostics;

using Andromeld.Desktop.Services;

using Avalonia.Labs.Notifications;
using Avalonia.Threading;

using Tmds.DBus.Protocol;

namespace Andromeld.Desktop.Tests;

internal static class LinuxNotificationChecks
{
    private const string RateLimitError = "org.freedesktop.Notifications.Error.ExcessNotificationGeneration";

    internal static async Task RunAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux()) return;
        // A private bus keeps synthetic traffic and service ownership away from the user's desktop.
        using Process bus = Process.Start(new ProcessStartInfo("dbus-daemon")
        {
            ArgumentList = { "--session", "--nofork", "--print-address=1" }, RedirectStandardOutput = true, UseShellExecute = false
        }) ?? throw new IOException("Unable to start the isolated notification bus");
        try
        {
            string address = await bus.StandardOutput.ReadLineAsync(ct) ?? throw new IOException("Missing private bus address");
            using DBusConnection serverConnection = new(address);
            await serverConnection.ConnectAsync();
            Server server = new();
            serverConnection.AddMethodHandler(server);
            await serverConnection.RequestNameAsync("org.freedesktop.Notifications");
            using LinuxNotificationManager manager = new(address);
            List<Exception> errors = [];
            manager.Failed += errors.Add;
            await manager.Pending.WaitAsync(ct);
            INativeNotification toast = manager.CreateNotification(null) ?? throw new IOException("Backend did not initialize");
            toast.Title = "合成通知限流测试";
            for (int i = 0; i < 12; i++)
            {
                toast.Message = "更新 " + i;
                toast.Show();
            }

            await manager.Pending.WaitAsync(ct);
            Program.Check(errors.Count == 0 && server.Calls.Count <= 2 && server.Calls[^1].Body == "更新 11" &&
                server.Calls.Skip(1).All(call => call.ReplacesId == server.Accepted[0]),
                "Linux backend coalesces queued updates to their latest content and preserves the server replacement ID");

            int calls = server.Calls.Count;
            INativeNotification duplicate = manager.CreateNotification(null) ?? throw new IOException("Missing notification");
            duplicate.Title = toast.Title;
            duplicate.Message = toast.Message;
            duplicate.Show();
            await manager.Pending.WaitAsync(ct);
            Program.Check(server.Calls.Count == calls + 1 && server.Rejected == 0 &&
                Stopwatch.GetElapsedTime(server.Calls[^2].Timestamp, server.Calls[^1].Timestamp) >= TimeSpan.FromSeconds(1),
                "identical notifications with different phone keys are paced past Plasma's one-second rejection window");

            calls = server.Calls.Count;
            server.NextError = RateLimitError;
            toast.Message = "限流后恢复";
            toast.Show();
            await manager.Pending.WaitAsync(ct);
            Program.Check(server.Calls.Count == calls + 2 && server.Calls[^1].Body == "限流后恢复" && errors.Count == 0 && manager.LastError is null &&
                Stopwatch.GetElapsedTime(server.Calls[^2].Timestamp, server.Calls[^1].Timestamp) >= TimeSpan.FromSeconds(1),
                "a transient native rate-limit response backs off and retries without a persistent sync error");

            calls = server.Calls.Count;
            toast.Message = "排队后撤回";
            toast.Show();
            toast.Close();
            await manager.Pending.WaitAsync(ct);
            Program.Check(server.Calls.Count == calls && server.Closed.Contains(server.Accepted[0]) && !manager.ActiveNotifications.ContainsKey(toast.Id),
                "withdrawing a queued update skips its Notify call and still closes the existing native notification");

            server.NextError = "org.freedesktop.Notifications.Error.AccessDenied";
            duplicate.Message = "合成权限失败";
            duplicate.Show();
            await manager.Pending.WaitAsync(ct);
            Program.Check(server.Calls.Count == calls + 1 && errors is [DBusErrorReplyException { ErrorName: "org.freedesktop.Notifications.Error.AccessDenied" }],
                "native errors other than rate limiting remain visible and are not retried as notification spam");

            calls = server.Calls.Count;
            duplicate.Message = "退出时取消排队";
            duplicate.Show();
            manager.Dispose();
            await manager.Pending.WaitAsync(ct);
            Program.Check(server.Calls.Count == calls && errors.Count == 1, "disposing the Linux backend cancels delayed sends without late platform access");
        }
        finally
        {
            if (!bus.HasExited) bus.Kill(entireProcessTree: true);
            await bus.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private sealed record Call(uint ReplacesId, string Title, string Body, string[] Actions, long Timestamp);

    // UI-owned state; D-Bus method dispatch only transfers the retained context to the UI thread.
    private sealed class Server : IPathMethodHandler
    {
        private Call? _lastAccepted;
        private uint _nextId;
        public string Path => "/org/freedesktop/Notifications";
        public bool HandlesChildPaths => false;
        internal List<Call> Calls { get; } = [];
        internal List<uint> Accepted { get; } = [];
        internal List<uint> Closed { get; } = [];
        internal string? NextError { get; set; }
        internal int Rejected { get; private set; }

        public ValueTask HandleMethodAsync(MethodContext context)
        {
            context.DisposesAsynchronously = true;
            Dispatcher.UIThread.Post(() => { using (context) Handle(context); });
            return ValueTask.CompletedTask;
        }

        private void Handle(MethodContext context)
        {
            Dispatcher.UIThread.VerifyAccess();
            var reader = context.Request.GetBodyReader();
            if (context.Request.MemberAsString == "CloseNotification")
            {
                Closed.Add(reader.ReadUInt32());
                using var reply = context.CreateReplyWriter(null);
                context.Reply(reply.CreateMessage());
                return;
            }

            if (context.Request.MemberAsString != "Notify") { context.ReplyUnknownMethodError(); return; }
            reader.ReadString();
            uint replacesId = reader.ReadUInt32();
            reader.ReadString();
            Call call = new(replacesId, reader.ReadString(), reader.ReadString(), reader.ReadArrayOfString(), Stopwatch.GetTimestamp());
            Calls.Add(call);
            string? error = NextError;
            NextError = null;
            // Plasma ignores replaces_id when checking consecutive identical notifications.
            if (error is null && _lastAccepted is { } last && call.Title == last.Title && call.Body == last.Body &&
                call.Actions.SequenceEqual(last.Actions) && Stopwatch.GetElapsedTime(last.Timestamp, call.Timestamp) < TimeSpan.FromSeconds(1)) error = RateLimitError;
            if (error is not null)
            {
                Rejected++;
                context.ReplyError(error, "Synthetic notification rejection");
                return;
            }

            _lastAccepted = call;
            uint id = replacesId == 0 ? ++_nextId : replacesId;
            Accepted.Add(id);
            using var writer = context.CreateReplyWriter("u");
            writer.WriteUInt32(id);
            context.Reply(writer.CreateMessage());
        }
    }
}
