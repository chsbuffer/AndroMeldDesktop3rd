using Andromeld.Protocol;

namespace Andromeld.Discovery;

/// <summary>One forward, retained until all control and media sockets have been disposed. Dispose once, serially.</summary>
public interface IUsbConnection : IAsyncDisposable
{
    Uri Endpoint { get; }
    UsbBootstrap Bootstrap { get; }
}