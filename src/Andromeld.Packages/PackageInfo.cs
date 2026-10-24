namespace Andromeld.Packages;

/// <summary>Local package metadata. Certificate identity is inspection data, not signature verification.</summary>
public sealed record PackageInfo(string PackageName, string Label, string VersionName, long VersionCode,
    int MinSdk, int? TargetSdk, string? SplitName, string[] Permissions, PackageCertificate[] Certificates, byte[]? Icon,
    PackageVectorIcon? VectorIcon = null);
