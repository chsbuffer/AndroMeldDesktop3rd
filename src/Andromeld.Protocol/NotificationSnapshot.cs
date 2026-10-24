namespace Andromeld.Protocol;

/// <summary>Full replacement snapshot; hidden and recent entries must not produce desktop toasts.</summary>
public sealed record NotificationSnapshot(IReadOnlyList<PhoneNotification> Active, bool Truncated);
