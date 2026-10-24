using Andromeld.Protocol;

namespace Andromeld.Desktop.Services;

/// <summary>All methods, including native initialization and disposal, belong to the UI thread.</summary>
public interface IAudioPlayback : IDisposable
{
    void Configure(AudioConfiguration configuration);
    void Write(ReadOnlySpan<byte> pcm);
}