using System.Buffers.Binary;
using System.Text.Json;

namespace Andromeld.Protocol;

/// <summary>LAN audio is signed interleaved S16LE, preceded by an unsigned big-endian timestamp.</summary>
public static class AudioMessageCodec
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static AudioConfiguration ReadConfiguration(ReadOnlyMemory<byte> payload)
    {
        AudioConfiguration configuration = JsonSerializer.Deserialize<AudioConfiguration>(payload.Span, Json)
                                           ?? throw new IOException("音频配置为空。");
        configuration.Validate();
        return configuration;
    }

    public static AudioPacket ReadPacket(AudioConfiguration configuration, ReadOnlyMemory<byte> payload)
    {
        configuration.Validate();
        if (payload.Length < 8 || payload.Length > 1048584 || (payload.Length - 8) % configuration.BytesPerFrame != 0)
        {
            throw new IOException("音频帧长度无效。");
        }

        return new AudioPacket(configuration, BinaryPrimitives.ReadUInt64BigEndian(payload.Span), payload[8..]);
    }
}