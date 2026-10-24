namespace Andromeld.Discovery;

public sealed record LanDevice(string Id, string Instance, string Name, IReadOnlyList<LanEndpoint> Endpoints)
{
    public string Key => $"{Id}/{Instance}";
}