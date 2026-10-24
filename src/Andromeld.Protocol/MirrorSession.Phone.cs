using System.Text.Json;

namespace Andromeld.Protocol;

public sealed partial class MirrorSession
{
    /// <summary>Media-reader notifications; presentation owners marshal these to their own thread.</summary>
    public event Action<FocusedApplication>? FocusedApplicationChanged;
    public event Action<bool>? DisplayPowerChanged;
    public event Action<DisplayPowerState>? DisplayPowerStateChanged;

    public Task SetDisplayPowerAsync(bool on, CancellationToken ct)
    {
        return State.IsPhone ? SendControlAsync(new { c = "display-power", on }, ct) : Task.CompletedTask;
    }

    private bool HandlePhoneControl(JsonElement value)
    {
        switch (value.TryGetProperty("c", out JsonElement command) ? command.GetString() : null)
        {
            case "focused-app":
                string? package = value.TryGetProperty("pkg", out JsonElement pkg) && pkg.ValueKind == JsonValueKind.String
                    ? pkg.GetString() : null;
                int userId = value.TryGetProperty("userId", out JsonElement user) && user.ValueKind == JsonValueKind.Number &&
                    user.TryGetInt32(out int id) ? Math.Max(0, id) : 0;
                FocusedApplicationChanged?.Invoke(new FocusedApplication(package, userId));
                return true;
            case "display-power" when State.IsPhone:
                DisplayPowerChanged?.Invoke(!value.TryGetProperty("on", out JsonElement on) || on.ValueKind != JsonValueKind.False);
                return true;
            case "display-power-state" when State.IsPhone:
                if (value.Deserialize<DisplayPowerState>(Json) is { } state) DisplayPowerStateChanged?.Invoke(state);
                return true;
            default:
                return false;
        }
    }
}
