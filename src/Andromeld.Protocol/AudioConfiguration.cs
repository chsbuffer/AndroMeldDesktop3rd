namespace Andromeld.Protocol;

public sealed record AudioConfiguration(int SampleRate, int Channels, string Format)
{
    public int BytesPerFrame => Channels * 2;

    public void Validate()
    {
        if (SampleRate is < 8000 or > 192000 || Channels is not (1 or 2) || !string.Equals(Format, "pcm_s16le", StringComparison.Ordinal))
        {
            throw new IOException("手机返回了不支持的音频配置。");
        }
    }
}