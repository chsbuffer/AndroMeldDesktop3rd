namespace Andromeld.Protocol;

public sealed record PhoneNotificationAction(int Index = -1, string Title = "", bool HasRemoteInput = false);
