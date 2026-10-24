using System.Net;
using System.Net.WebSockets;
using System.Buffers.Binary;
using System.Text.Json;
using Andromeld.Protocol;
using Andromeld.Testing;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace Andromeld.Protocol.Tests;

public static partial class Program
{
    private static int _checks;
    internal static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        _checks++; Console.WriteLine("PASS: " + name);
    }
    public static async Task Main(string[] args)
    {
        if (args.Contains("--installation"))
        {
            await CheckInstallationAsync();
            Console.WriteLine($"{_checks} installation protocol checks passed.");
            return;
        }
        if (args.Contains("--notifications"))
        {
            await CheckNotificationsAsync();
            Console.WriteLine($"{_checks} notification protocol checks passed.");
            return;
        }
        if (args.Contains("--phone-mirror"))
        {
            await CheckPhoneMirrorControlsAsync();
            Console.WriteLine($"{_checks} phone mirror protocol checks passed.");
            return;
        }
        Check(ConnectCode.Ticket("1955-JNBT-WANX") == "39U6DY5U", "connect code agrees with original Web vector");
        try { ConnectCode.Ticket("1955-JNBT-WANY"); throw new Exception("Bad checksum accepted"); }
        catch (FormatException) { Check(true, "connect code rejects changed checksum"); }
        CheckEndpoints();
        CheckUsbBootstrap();
        await CheckIntentsAsync();
        await using var server = await MockPhone.StartAsync();
        await using var client = new AndromeldClient();
        await client.ConnectAsync(server.Endpoint, "synthetic-test-client", null, null, CancellationToken.None);
        Check(client.DeviceName == "模拟手机" && client.Token == "test-token", "pair / fragmented welcome / capability negotiation");
        await CheckDeviceInformationAsync(client, server);
        var apps = await client.GetAppsAsync(CancellationToken.None);
        Check(apps.Length == 18 && apps[0].DisplayName == "相册", "app-list with Unicode labels");
        var icons = new List<AppIcon>();
        foreach (var batch in apps.Chunk(16)) icons.AddRange(await client.GetIconsAsync(batch, CancellationToken.None));
        Check(icons.Count == 18 && server.IconBatches.SequenceEqual([16, 2]), "icon batches stay within 16 items and preserve user IDs");
        var roots = await client.GetRootsAsync(CancellationToken.None);
        var files = await client.ListAsync(roots[0].Path, CancellationToken.None);
        Check(files.Length == 201 && files[0].IsDir && server.SawStringCursor, "directory pagination and folders first");
        try { await client.ListAsync(roots[0].Path + "/denied", CancellationToken.None); throw new Exception("Expected access error"); }
        catch (IOException ex) { Check(ex.Message.Contains("eacces"), "file error responses remain visible"); }
        using var destination = new MemoryStream();
        await client.DownloadAsync(roots[0].Path + "/sample.bin", destination, null, CancellationToken.None);
        Check(destination.ToArray().SequenceEqual(MockPhone.Payload), "download handles begin before operation result and fragmented chunks");
        await server.FinalAck.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(server.Acks.SequenceEqual([262144L, 524288L, 600000L, 600000L]), "download ACKs cumulative bytes and acknowledges end");
        using var empty = new MemoryStream();
        await client.DownloadAsync(roots[0].Path + "/empty", empty, null, CancellationToken.None);
        Check(empty.Length == 0, "zero byte download finishes with final ACK");
        using var truncated = new MemoryStream();
        try { await client.DownloadAsync(roots[0].Path + "/truncated", truncated, null, CancellationToken.None); throw new Exception("Expected truncation"); }
        catch (IOException) { Check(true, "truncated download is rejected"); }
        await using var resumed = new AndromeldClient();
        await resumed.ConnectAsync(server.Endpoint, "synthetic-test-client", "test-token", null, CancellationToken.None);
        Check(resumed.Token == "test-token", "resume welcome without token retains stored credential");
        await using var denied = new AndromeldClient();
        try { await denied.ConnectAsync(server.Endpoint, "synthetic-test-client", "bad-token", null, CancellationToken.None); throw new Exception("Expected denial"); }
        catch (AuthenticationDeniedException ex) { Check(ex.Reason == "UNAUTHORIZED" && ex.Message.Contains("凭据"), "authentication denial preserves the protocol reason and actionable error"); }
        await CheckMirroringAsync(client, server, apps[0]);
        await CheckPhoneMirrorControlsAsync();
        await CheckFileDragAsync(client, server, apps[0]);
        await CheckScreenCameraAsync(client, server, apps[0]);
        await CheckMirrorCloseAsync(client, server, apps[0]);
        await CheckClipboardAsync(client, server);
        await CheckNotificationsAsync();
        await CheckAudioAsync(client, server, apps[0]);
        await CheckTerminalsAsync(client, server);
        await CheckFileCreateAsync(client, server, roots[0].Path);
        await CheckInstallationAsync();
        await CheckFileDeleteAsync(client, server, roots[0].Path);
        try { await client.ListAsync(roots[0].Path + "/drop", CancellationToken.None); throw new Exception("Expected disconnect"); }
        catch (IOException) { Check(true, "disconnect fails pending requests immediately"); }
        Console.WriteLine($"{_checks} protocol checks passed.");
    }

}
