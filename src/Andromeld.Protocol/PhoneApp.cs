namespace Andromeld.Protocol;

public sealed record PhoneApp(string PackageName, string DisplayName, int UserId, bool IsSystemApp, int AppCategory, bool Game);