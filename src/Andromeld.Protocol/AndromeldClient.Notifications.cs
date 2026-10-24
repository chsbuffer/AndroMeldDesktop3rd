using System.Text.Json;

namespace Andromeld.Protocol;

public sealed partial class AndromeldClient
{
    /// <summary>Raised on the control reader; subscribers marshal to their presentation thread.</summary>
    public event Action<NotificationSnapshot>? NotificationsReceived;

    public async Task SetNotificationsEnabledAsync(bool enabled, CancellationToken ct)
    {
        string id = Guid.NewGuid().ToString("N");
        JsonElement reply = await RequestAsync(id, new { c = "notif-subscribe", reqId = id, on = enabled }, ct).ConfigureAwait(false);
        if (reply.Deserialize(NotificationJsonContext.Default.NotificationSubscriptionState)?.On != enabled)
        {
            throw new IOException("手机未能切换通知订阅，请检查通知使用权。");
        }
    }

    public Task DismissNotificationAsync(string key, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        string id = Guid.NewGuid().ToString("N");
        return RequestAsync(id, new { c = "notif-cmd", reqId = id, command = 2, key }, ct);
    }

    public Task OpenNotificationAsync(PhoneNotification notification, string? sessionId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(notification);
        if (string.IsNullOrEmpty(notification.Key)) throw new ArgumentException("通知 key 不能为空。", nameof(notification));
        string id = Guid.NewGuid().ToString("N");
        object request = sessionId is null
            ? new { c = "notif-cmd", reqId = id, command = 6, key = notification.Key }
            : (object)new { c = "notif-cmd", reqId = id, command = 6, key = notification.Key, sessionId, fallbackPkg = notification.PackageName, fallbackUserId = notification.UserId };
        return RequestAsync(id, request, ct);
    }

    public Task ReplyToNotificationAsync(string key, int actionIndex, string replyText, string sessionId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (key.Length > 512) throw new ArgumentException("通知 key 不能超过 512 个字符。", nameof(key));
        ArgumentOutOfRangeException.ThrowIfNegative(actionIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(actionIndex, 64);
        ArgumentException.ThrowIfNullOrWhiteSpace(replyText);
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        if (sessionId.Length > 64) throw new ArgumentException("镜像会话 ID 不能超过 64 个字符。", nameof(sessionId));
        replyText = replyText.Trim();
        // Match the Web client's limit, preserving a complete UTF-16 surrogate pair at the boundary.
        if (replyText.Length > 4096)
        {
            int length = char.IsHighSurrogate(replyText[4095]) && char.IsLowSurrogate(replyText[4096]) ? 4095 : 4096;
            replyText = replyText.Substring(0, length);
        }

        string id = Guid.NewGuid().ToString("N");
        return RequestAsync(id, new { c = "notif-cmd", reqId = id, command = 8, key, actionIndex, replyText, sessionId }, ct);
    }

    private void HandleNotificationSnapshot(JsonElement value)
    {
        NotificationSnapshotMessage? message;
        try { message = value.Deserialize(NotificationJsonContext.Default.NotificationSnapshotMessage); }
        catch (JsonException) { return; }
        if (message?.Active is not { } active)
        {
            return;
        }

        List<PhoneNotification> notifications = [];
        foreach (JsonElement item in active)
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            try
            {
                PhoneNotification? notification = item.Deserialize(NotificationJsonContext.Default.PhoneNotification);
                if (notification is { Key.Length: > 0 and <= 512, PackageName.Length: > 0, UserId: >= 0, RemovedAt: 0, SystemHidden: false })
                {
                    IReadOnlyList<PhoneNotificationAction> actions = Array.AsReadOnly(notification.Actions?
                        .Where(static action => action is { Index: >= 0 and <= 64 }).ToArray() ?? []);
                    notifications.Add(notification with { Actions = actions, ContentIntentTargetKind = notification.ContentIntentTargetKind ?? "unknown" });
                }
            }
            catch (JsonException)
            {
                // One malformed notification must not disconnect unrelated mirrors/files/terminal sessions.
            }
        }

        NotificationsReceived?.Invoke(new NotificationSnapshot(notifications.AsReadOnly(), message.Truncated));
    }
}
