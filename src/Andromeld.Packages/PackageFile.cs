namespace Andromeld.Packages;

public sealed record PackageFile(string Path, string ArchiveName, long Size, PackageInfo Info, string[] Abis);
