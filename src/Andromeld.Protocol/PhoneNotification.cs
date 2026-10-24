namespace Andromeld.Protocol;

/// <summary>A notification from an authenticated device's notif-snapshot.</summary>
public sealed record PhoneNotification
{
    public string Key { get; init; } = "";
    public string PackageName { get; init; } = "";
    public int UserId { get; init; }
    public string Title { get; init; } = "";
    public string Text { get; init; } = "";
    public string SubText { get; init; } = "";
    public string BigText { get; init; } = "";
    public long PostTime { get; init; }
    public long UpdateTime { get; init; }
    public long RemovedAt { get; init; }
    public bool Clearable { get; init; }
    public bool SystemHidden { get; init; }
    public string ContentIntentTargetKind { get; init; } = "unknown";
    public IReadOnlyList<PhoneNotificationAction> Actions { get; init; } = [];
}
