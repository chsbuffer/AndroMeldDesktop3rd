using System.Text.Json.Serialization;

namespace Andromeld.Protocol;

public sealed record FocusedApplication([property: JsonPropertyName("pkg")] string? PackageName, int UserId = 0);
