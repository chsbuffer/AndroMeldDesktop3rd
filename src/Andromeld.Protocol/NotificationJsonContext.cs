using System.Text.Json.Serialization;

namespace Andromeld.Protocol;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(PhoneNotification))]
[JsonSerializable(typeof(NotificationSubscriptionState))]
[JsonSerializable(typeof(NotificationSnapshotMessage))]
internal sealed partial class NotificationJsonContext : JsonSerializerContext;
