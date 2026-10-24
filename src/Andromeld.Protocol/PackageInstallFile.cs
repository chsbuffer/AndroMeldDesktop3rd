namespace Andromeld.Protocol;

/// <summary>The caller keeps local sources stable until installation completes.</summary>
public sealed record PackageInstallFile(long Size, Func<Stream> OpenRead);
