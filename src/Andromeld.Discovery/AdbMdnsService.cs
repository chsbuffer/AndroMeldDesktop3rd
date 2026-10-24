using System.Net;

namespace Andromeld.Discovery;

internal sealed record AdbMdnsService(string Instance, string Type, IPAddress Address, int Port);
