namespace Andromeld.Protocol;

public sealed record PackageInstallTarget(int Sdk, string[] Abis, int Density = 0);
