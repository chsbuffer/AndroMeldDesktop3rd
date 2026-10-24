using Tmds.MDns;

namespace Andromeld.Discovery;

public sealed class LanDiscovery : ILanDiscovery
{
    public const string ServiceType = "_andromeld._tcp";
    private readonly DiscoveryCatalog _catalog = new();
    private readonly int _ownerThread = Environment.CurrentManagedThreadId;
    private ServiceBrowser? _browser;
    private bool _disposed;

    public event EventHandler? DevicesChanged;

    public IReadOnlyList<LanDevice> Devices
    {
        get
        {
            VerifyAccess();
            return _catalog.Snapshot();
        }
    }

    public void Start()
    {
        VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_browser is not null)
        {
            return;
        }

        SynchronizationContext context = SynchronizationContext.Current
                                         ?? throw new InvalidOperationException(
                                             "Discovery requires its owner's single-thread SynchronizationContext.");
        ServiceBrowser browser = new();
        // Tmds uses a query/response liveness policy, not DNS TTL expiry. Keep querying the phone,
        // whose periodic announcement interval (15 min) is longer than its advertised TTL (120 s).
        browser.QueryParameters.StartQueryInterval = 2000;
        browser.QueryParameters.QueryInterval = 10000;
        browser.QueryParameters.ResponseTime = 2000;
        browser.QueryParameters.Robustness = 3;
        browser.ServiceAdded += ServiceUpdated;
        browser.ServiceChanged += ServiceUpdated;
        browser.ServiceRemoved += ServiceRemoved;
        _browser = browser;
        try { browser.StartBrowse(ServiceType, context); }
        catch
        {
            Stop();
            throw;
        }
    }

    public void Refresh()
    {
        Stop();
        Start();
    }

    public void Dispose()
    {
        VerifyAccess();
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }

    private void ServiceUpdated(object? sender, ServiceAnnouncementEventArgs e)
    {
        VerifyAccess();
        if (!ReferenceEquals(sender, _browser))
        {
            return;
        }

        ServiceAnnouncement announcement = e.Announcement;
        _catalog.Update(announcement.Type, announcement.Instance, announcement.Port, announcement.Txt,
            announcement.Addresses, announcement.NetworkInterface.Id, announcement.NetworkInterface.Name);
        DevicesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ServiceRemoved(object? sender, ServiceAnnouncementEventArgs e)
    {
        VerifyAccess();
        if (!ReferenceEquals(sender, _browser))
        {
            return;
        }

        _catalog.Remove(e.Announcement.NetworkInterface.Id, e.Announcement.Instance);
        DevicesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Stop()
    {
        VerifyAccess();
        ServiceBrowser? browser = _browser;
        _browser = null;
        _catalog.Clear();
        if (browser is not null)
        {
            browser.ServiceAdded -= ServiceUpdated;
            browser.ServiceChanged -= ServiceUpdated;
            browser.ServiceRemoved -= ServiceRemoved;
            browser.StopBrowse();
        }

        DevicesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void VerifyAccess()
    {
        if (Environment.CurrentManagedThreadId != _ownerThread)
        {
            throw new InvalidOperationException("LanDiscovery must be accessed on its owning thread.");
        }
    }
}