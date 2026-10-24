using System.Text.Json;

namespace Andromeld.Protocol;

public sealed partial class AndromeldClient
{
    // Only the control reader owns this snapshot. Consumers receive it with each packet.
    private AudioConfiguration? _audioConfiguration;
    /// <summary>Raised on the control reader; handlers must not block control/clipboard/terminal traffic.</summary>
    public event Action<AudioPacket>? AudioReceived;
    public event Action<Exception>? AudioFailed;

    public Task SetAudioEnabledAsync(bool enabled, string? sessionId, CancellationToken ct)
    {
        if (enabled && string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("音频转发需要镜像会话。", nameof(sessionId));
        }

        return SendJsonAsync(new { on = enabled, sessionId = enabled ? sessionId : "" }, false, 2, 3, ct);
    }

    private void HandleAudio(byte type, ReadOnlyMemory<byte> payload)
    {
        try
        {
            if (type == 1)
            {
                _audioConfiguration = AudioMessageCodec.ReadConfiguration(payload);
            }
            else if (type == 2 && _audioConfiguration is { } configuration)
            {
                AudioReceived?.Invoke(AudioMessageCodec.ReadPacket(configuration, payload));
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _audioConfiguration = null;
            AudioFailed?.Invoke(ex);
        }
    }
}