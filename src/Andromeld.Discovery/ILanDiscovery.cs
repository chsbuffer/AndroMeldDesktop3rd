namespace Andromeld.Discovery;

/// <summary>
///     Owned by the creating thread. All members and events run on that thread, with its single-thread
///     SynchronizationContext installed. Returned device collections are detached snapshots.
/// </summary>
public interface ILanDiscovery : IDisposable
{
    IReadOnlyList<LanDevice> Devices { get; }
    event EventHandler? DevicesChanged;

    void Start();

    void Refresh();
}