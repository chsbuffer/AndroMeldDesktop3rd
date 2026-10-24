using System.Net;

namespace Andromeld.Discovery;

public sealed record LanEndpoint(IPAddress Address, int AgentPort, string InterfaceId, string InterfaceName);