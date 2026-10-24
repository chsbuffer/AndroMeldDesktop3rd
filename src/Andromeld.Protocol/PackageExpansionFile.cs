namespace Andromeld.Protocol;

public sealed record PackageExpansionFile(string Name, long Size, Func<Stream> OpenRead);
