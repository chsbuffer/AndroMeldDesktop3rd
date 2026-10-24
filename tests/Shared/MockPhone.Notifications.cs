using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;

namespace Andromeld.Testing;

public sealed partial class MockPhone
{
    private WebSocket? _notificationSocket;
    public bool SupportsNotifications { get; set; } = true;
    public string? NotificationSubscriptionError { get; set; }
    public string? NotificationCommandError { get; set; }
    public TaskCompletionSource? NotificationSubscriptionGate { get; set; }
    public ConcurrentQueue<JsonElement> NotificationRequests { get; } = new();
    public object[] InitialNotifications { get; set; } = [];

    public Task PushNotificationsAsync(params object[] active) => SendAsync(
        _notificationSocket ?? throw new InvalidOperationException("Notifications are not subscribed"),
        new { c = "notif-snapshot", active, hidden = Array.Empty<object>(), recent = Array.Empty<object>(), generatedAt = 1000, truncated = false });

    private async Task HandleNotificationsAsync(WebSocket socket, JsonElement request)
    {
        NotificationRequests.Enqueue(request.Clone());
        string? id = request.GetProperty("reqId").GetString();
        if (string.IsNullOrEmpty(id) || id.Length > 64) throw new IOException("Notifications require a bounded reqId");
        if (request.GetProperty("c").GetString() == "notif-subscribe")
        {
            bool on = request.GetProperty("on").GetBoolean();
            if (NotificationSubscriptionGate is { } gate) await gate.Task;
            await SendAsync(socket, new { c = "notif-state", reqId = id, on = on && NotificationSubscriptionError is null, err = NotificationSubscriptionError });
            if (on && NotificationSubscriptionError is null)
            {
                _notificationSocket = socket;
                await PushNotificationsAsync(InitialNotifications);
            }
        }
        else
        {
            await SendAsync(socket, new { c = "notif-cmd-result", reqId = id, command = request.GetProperty("command").GetInt32(),
                key = request.GetProperty("key").GetString(), ok = NotificationCommandError is null, err = NotificationCommandError });
        }
    }
}
