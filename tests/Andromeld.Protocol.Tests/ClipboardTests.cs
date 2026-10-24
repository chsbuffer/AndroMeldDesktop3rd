using Andromeld.Protocol;
using Andromeld.Testing;

namespace Andromeld.Protocol.Tests;

public static partial class Program
{
    private static async Task CheckClipboardAsync(AndromeldClient client, MockPhone server)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Receive(string text) => received.TrySetResult(text);
        client.ClipboardReceived += Receive;
        try
        {
            await client.SetClipboardEnabledAsync(true, stop.Token);
            Check(await received.Task.WaitAsync(stop.Token) == MockPhone.RemoteClipboard,
                "clipboard subscription [3,3] receives Unicode phone text on [3,1]");
            await client.SetClipboardTextAsync("电脑→手机\n✓", stop.Token);
            await UntilAsync(() => server.ClipboardMessages.Any(m => m.Type == 2), stop.Token);
            Check(server.ClipboardMessages.Single(m => m.Type == 2).Message.GetProperty("text").GetString() == "电脑→手机\n✓",
                "desktop clipboard uses [3,2] JSON without a control command");
            await client.SetClipboardEnabledAsync(false, stop.Token);
            await UntilAsync(() => server.ClipboardMessages.Any(m => m.Type == 3 && !m.Message.GetProperty("on").GetBoolean()), stop.Token);
            Check(true, "clipboard subscription stops on [3,3] on=false");
        }
        finally { client.ClipboardReceived -= Receive; }
    }
}
