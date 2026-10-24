namespace Andromeld.Protocol;

public sealed record PackageInstallProgress(string Message, long Sent = 0, long Total = 0);
