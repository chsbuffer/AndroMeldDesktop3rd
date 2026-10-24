using System.Net.WebSockets;
using System.Threading.Channels;

using Andromeld.Protocol;

using Avalonia.Threading;

namespace Andromeld.Desktop.Services;

/// <summary>UI owns playback and lifecycle. The control reader only writes to a bounded, nonblocking mailbox.</summary>
public sealed class AudioSharing : IAsyncDisposable
{
    private readonly AndromeldClient _client;
    private readonly Action<Exception> _onError;
    private readonly Channel<AudioPacket> _packets = Channel.CreateBounded<AudioPacket>(new BoundedChannelOptions(8)
    {
        SingleWriter = true, SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest
    });
    private readonly Func<IAudioPlayback> _playbackFactory;
    private readonly CancellationTokenSource _stop;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(10) };
    private bool _disposed, _failed;
    private IAudioPlayback? _playback;
    private string? _sessionId;

    public AudioSharing(AndromeldClient client, Action<Exception> onError, CancellationToken connection,
        Func<IAudioPlayback>? playbackFactory = null)
    {
        Dispatcher.UIThread.VerifyAccess();
        _client = client;
        _onError = onError;
        _playbackFactory = playbackFactory ?? (static () => new SdlAudioPlayback());
        _stop = CancellationTokenSource.CreateLinkedTokenSource(connection);
        _client.AudioReceived += Receive;
        _client.AudioFailed += ProtocolFailed;
        _timer.Tick += Tick;
    }

    public bool IsStopped => _disposed || _failed;

    public async ValueTask DisposeAsync()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _timer.Tick -= Tick;
        _client.AudioReceived -= Receive;
        _client.AudioFailed -= ProtocolFailed;
        _packets.Writer.TryComplete();
        while (_packets.Reader.TryRead(out _))
        {
            /* Do not replay queued samples after disabling. */
        }

        _playback?.Dispose();
        _playback = null;
        await _stop.CancelAsync().ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
        try { await _client.SetAudioEnabledAsync(false, null, timeout.Token).ConfigureAwait(true); }
        catch (Exception ex) when (ex is IOException or WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            /* The connection may already be gone. */
        }

        _stop.Dispose();
    }

    public async Task SetSessionAsync(string sessionId)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_disposed || _failed || string.Equals(_sessionId, sessionId, StringComparison.Ordinal))
        {
            return;
        }

        await _client.SetAudioEnabledAsync(true, sessionId, _stop.Token).ConfigureAwait(true);
        if (_disposed || _stop.IsCancellationRequested)
        {
            return;
        }

        _sessionId = sessionId;
        _timer.Start();
    }

    private void Receive(AudioPacket packet)
    {
        _packets.Writer.TryWrite(packet);
    }

    private void ProtocolFailed(Exception ex)
    {
        Dispatcher.UIThread.Post(() => Fail(ex));
    }

    private void Tick(object? sender, EventArgs e)
    {
        if (_disposed || _failed || _stop.IsCancellationRequested)
        {
            return;
        }

        try
        {
            for (int i = 0; i < 8 && _packets.Reader.TryRead(out AudioPacket? packet); i++)
            {
                _playback ??= _playbackFactory();
                _playback.Configure(packet.Configuration);
                _playback.Write(packet.Data.Span);
            }
        }
        catch (Exception ex) { Fail(ex); }
    }

    private void Fail(Exception ex)
    {
        if (_disposed || _failed)
        {
            return;
        }

        StopPlayback();
        _onError(ex);
        _ = DisableAfterFailureAsync();
    }

    public void StopPlayback()
    {
        Dispatcher.UIThread.VerifyAccess();
        _failed = true;
        _timer.Stop();
        _packets.Writer.TryComplete();
        while (_packets.Reader.TryRead(out _))
        {
            /* Discard queued samples immediately. */
        }

        _playback?.Dispose();
        _playback = null;
    }

    private async Task DisableAfterFailureAsync()
    {
        try { await _client.SetAudioEnabledAsync(false, null, _stop.Token).ConfigureAwait(true); }
        catch (Exception ex) when (ex is IOException or WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            /* The connection may already be gone. */
        }
    }
}