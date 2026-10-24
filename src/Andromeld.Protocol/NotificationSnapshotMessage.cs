using System.Text.Json;

namespace Andromeld.Protocol;

internal sealed record NotificationSnapshotMessage(JsonElement[]? Active, bool Truncated);
