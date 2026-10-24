using Andromeld.Protocol;

using Avalonia.Threading;

using SDL = SDL3.SDL;

namespace Andromeld.Desktop.Services;

/// <summary>SDL resamples the phone's S16LE PCM for the default output device.</summary>
public sealed class SdlAudioPlayback : IAudioPlayback
{
    private AudioConfiguration? _configuration;
    private bool _initialized, _playing, _disposed;
    private nint _stream;

    public void Configure(AudioConfiguration configuration)
    {
        Dispatcher.UIThread.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        configuration.Validate();
        if (_configuration == configuration)
        {
            return;
        }

        ReleaseStream();
        if (!_initialized)
        {
            if (!SDL.InitSubSystem(SDL.InitFlags.Audio))
            {
                throw new IOException(SDL.GetError());
            }

            _initialized = true;
        }

        SDL.AudioSpec spec = new() { Format = SDL.AudioFormat.AudioS16LE, Channels = configuration.Channels, Freq = configuration.SampleRate };
        _stream = SDL.OpenAudioDeviceStream(SDL.AudioDeviceDefaultPlayback, in spec, null!, 0);
        if (_stream == 0)
        {
            throw new IOException(SDL.GetError());
        }

        _configuration = configuration;
    }

    public void Write(ReadOnlySpan<byte> pcm)
    {
        Dispatcher.UIThread.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_stream == 0 || _configuration is not { } configuration)
        {
            throw new InvalidOperationException("音频尚未配置。");
        }

        if (pcm.IsEmpty)
        {
            return;
        }

        if (pcm.Length % configuration.BytesPerFrame != 0)
        {
            throw new IOException("PCM 采样帧不完整。");
        }

        int maximum = configuration.SampleRate / 5 * configuration.BytesPerFrame; // Web's 200ms latency bound, in complete sample frames.
        if (pcm.Length > maximum)
        {
            pcm = pcm[^maximum..];
        }

        int queued = SDL.GetAudioStreamQueued(_stream);
        if (queued < 0)
        {
            throw new IOException(SDL.GetError());
        }

        if (queued + pcm.Length > maximum)
        {
            if (!SDL.PauseAudioStreamDevice(_stream) || !SDL.ClearAudioStream(_stream))
            {
                throw new IOException(SDL.GetError());
            }

            _playing = false;
            queued = 0;
        }

        if (!SDL.PutAudioStreamData(_stream, pcm, pcm.Length))
        {
            throw new IOException(SDL.GetError());
        }

        // Re-prime after an underrun instead of repeatedly playing tiny fragments.
        if (_playing && queued == 0)
        {
            if (!SDL.PauseAudioStreamDevice(_stream))
            {
                throw new IOException(SDL.GetError());
            }

            _playing = false;
        }

        if (!_playing && queued + pcm.Length >= configuration.SampleRate * 12 / 100 * configuration.BytesPerFrame)
        {
            if (!SDL.ResumeAudioStreamDevice(_stream))
            {
                throw new IOException(SDL.GetError());
            }

            _playing = true;
        }
    }

    public void Dispose()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ReleaseStream();
        if (_initialized)
        {
            SDL.QuitSubSystem(SDL.InitFlags.Audio);
        }

        _initialized = false;
    }

    private void ReleaseStream()
    {
        if (_stream != 0)
        {
            SDL.DestroyAudioStream(_stream);
        }

        _stream = 0;
        _configuration = null;
        _playing = false;
    }
}