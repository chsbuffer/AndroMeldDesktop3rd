using System.Text.Json;

namespace Andromeld.Protocol;

public sealed partial class AndromeldClient
{
    /// <summary>Raised on the control reader. Clipboard access belongs on the platform's UI thread.</summary>
    public event Action<string>? ClipboardReceived;

    public Task SetClipboardEnabledAsync(bool enabled, CancellationToken ct)
    {
        return SendJsonAsync(new { on = enabled }, false, 3, 3, ct);
    }

    public Task SetClipboardTextAsync(string text, CancellationToken ct)
    {
        return string.IsNullOrEmpty(text) ? Task.CompletedTask : SendJsonAsync(new { text }, false, 3, 2, ct);
    }

    private void HandleClipboard(ReadOnlyMemory<byte> bytes)
    {
        using JsonDocument doc = JsonDocument.Parse(bytes);
        if (doc.RootElement.TryGetProperty("text", out JsonElement text) && text.ValueKind == JsonValueKind.String)
        {
            ClipboardReceived?.Invoke(text.GetString()!);
        }
    }
}