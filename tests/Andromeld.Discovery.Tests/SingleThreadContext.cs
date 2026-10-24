using System.Threading.Channels;

namespace Andromeld.Discovery.Tests;

internal sealed class SingleThreadContext : SynchronizationContext
{
    private readonly Channel<(SendOrPostCallback Callback, object? State)> _work = Channel.CreateUnbounded<(SendOrPostCallback, object?)>(
        new UnboundedChannelOptions { SingleReader = true });

    public override void Post(SendOrPostCallback d, object? state) => _work.Writer.TryWrite((d, state));

    public static void Run(Func<Task> action)
    {
        var previous = Current;
        var context = new SingleThreadContext();
        SetSynchronizationContext(context);
        try
        {
            var task = action();
            _ = task.ContinueWith(_ => context._work.Writer.TryComplete(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            while (context._work.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
                while (context._work.Reader.TryRead(out var work)) work.Callback(work.State);
            task.GetAwaiter().GetResult();
        }
        finally { SetSynchronizationContext(previous); }
    }
}
