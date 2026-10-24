using System.Text;
using Andromeld.Protocol;
using Andromeld.Testing;

namespace Andromeld.Protocol.Tests;

public static partial class Program
{
    private static async Task CheckTerminalsAsync(AndromeldClient client, MockPhone phone)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = stop.Token;
        await using var terminal = await client.OpenTerminalAsync(80, 24, ct);
        var banner = (await terminal.ReadOutputAsync(ct)).ToArray().Concat((await terminal.ReadOutputAsync(ct)).ToArray()).ToArray();
        Check(Encoding.UTF8.GetString(banner) == MockPhone.TerminalBanner, "terminal multiplexes raw fragmented UTF-8 and ANSI over control socket");
        Check(!phone.TerminalCommands.Any(x => x.GetProperty("c").GetString() == "term-ack"), "receiving terminal bytes never acknowledges unparsed output");
        await terminal.AcknowledgeOutputAsync(banner.Length, ct);
        var flood = Enumerable.Repeat((byte)'x', 131072).ToArray();
        await phone.PushTerminalOutputAsync(terminal.Id, flood);
        Check((await client.GetAppsAsync(ct)).Length == 18, "a stalled terminal consumer does not block ordinary control replies");
        var consumed = banner.Length;
        while (consumed < banner.Length + flood.Length)
        {
            var bytes = await terminal.ReadOutputAsync(ct);
            consumed += bytes.Length;
            await terminal.AcknowledgeOutputAsync(bytes.Length, ct);
        }
        await client.GetAppsAsync(ct); // round-trip barrier for cumulative acknowledgements
        var acks = phone.TerminalCommands.Where(x => x.GetProperty("c").GetString() == "term-ack").ToArray();
        Check(acks.Length == 4 && acks[0].GetProperty("received").GetInt64() == banner.Length + 32768 &&
            acks[^1].GetProperty("received").GetInt64() == consumed, "terminal ACKs consumed raw payload bytes at 32 KiB cumulative intervals");
        var input = Encoding.UTF8.GetBytes("echo 测试\r\u0003\u001b[A\t");
        await terminal.SendInputAsync(input, ct);
        Check((await terminal.ReadOutputAsync(ct)).Span.SequenceEqual(input), "terminal input uses binary 06 01 and the big-endian session id");
        await phone.PushTerminalOutputAsync(terminal.Id, Enumerable.Repeat((byte)'p', 64).ToArray(), chunkSize: 1);
        await client.GetAppsAsync(ct);
        var smallChunks = 0;
        for (var i = 0; i < 64; i++) smallChunks += (await terminal.ReadOutputAsync(ct)).Length;
        Check(smallChunks == 64 && !terminal.IsClosed, "terminal queue is bounded by bytes rather than closing on many small legal frames");
        await terminal.ResizeAsync(120, 40, ct);
        foreach (var size in new[] { (0, 24), (501, 24), (80, 0), (80, 301) })
        {
            try { await terminal.ResizeAsync(size.Item1, size.Item2, ct); throw new Exception("Invalid dimensions accepted"); }
            catch (ArgumentOutOfRangeException) { }
        }
        await client.GetAppsAsync(ct);
        Check(phone.TerminalCommands.Count(x => x.GetProperty("c").GetString() == "term-resize") == 1,
            "invalid terminal dimensions are rejected before they can close the authenticated socket");
        var others = new List<TerminalSession>();
        for (var i = 0; i < 3; i++) others.Add(await client.OpenTerminalAsync(80, 24, ct));
        try { await client.OpenTerminalAsync(80, 24, ct); throw new Exception("Expected session limit"); }
        catch (IOException ex) { Check(ex.Message.Contains('4'), "fifth terminal surfaces APK session limit without disconnecting"); }
        foreach (var other in others) await other.DisposeAsync();
        var tail = Encoding.UTF8.GetBytes("\r\n最后一行\r\n");
        await phone.PushTerminalOutputAsync(terminal.Id, tail);
        await phone.ExitTerminalAsync(terminal.Id, 7);
        var exit = await terminal.Completion.WaitAsync(ct);
        Check(exit.Code == 7 && (await terminal.ReadOutputAsync(ct)).Span.SequenceEqual(tail) && (await terminal.ReadOutputAsync(ct)).IsEmpty,
            "term-exit retains queued output and reports the real exit status");

        phone.HoldTerminalOpen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = new CancellationTokenSource();
        var expected = phone.TerminalCommands.Count(x => x.GetProperty("c").GetString() == "term-open");
        var opening = client.OpenTerminalAsync(80, 24, cancel.Token);
        while (phone.TerminalCommands.Count(x => x.GetProperty("c").GetString() == "term-open") == expected) await Task.Delay(5, ct);
        cancel.Cancel();
        try { await opening; throw new Exception("Cancelled open succeeded"); }
        catch (OperationCanceledException) { }
        phone.HoldTerminalOpen.SetResult(); phone.HoldTerminalOpen = null;
        while (!phone.TerminalCommands.Any(x => x.GetProperty("c").GetString() == "term-close" && x.GetProperty("id").GetInt32() == 5)) await Task.Delay(5, ct);
        Check(true, "a successful late term-opened is closed even after the caller cancels");
        await using var restarted = await client.OpenTerminalAsync(90, 30, ct);
        Check(restarted.Id == 6 && client.Capabilities.Contains("terminal"), "terminal reopening allocates a fresh id on the same authenticated connection");
        await using var unsupportedPhone = await MockPhone.StartAsync();
        unsupportedPhone.SupportsTerminal = false;
        await using var unsupported = new AndromeldClient();
        await unsupported.ConnectAsync(unsupportedPhone.Endpoint, "unsupported-terminal-test", "test-token", null, ct);
        try { await unsupported.OpenTerminalAsync(80, 24, ct); throw new Exception("Missing capability accepted"); }
        catch (NotSupportedException) { Check(unsupportedPhone.TerminalCommands.IsEmpty, "unadvertised terminal capability sends no unsupported command"); }
    }
}
