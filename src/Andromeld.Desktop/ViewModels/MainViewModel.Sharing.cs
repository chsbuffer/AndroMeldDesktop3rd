using System.ComponentModel;

using Andromeld.Desktop.Services;
using Andromeld.Protocol;

using Avalonia.Threading;

using ReactiveUI.SourceGenerators;

namespace Andromeld.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private readonly Func<IAudioPlayback>? _audioPlaybackFactory;
    private MirrorViewModel? _activeMirror;
    private AudioSharing? _audioSharing;
    private bool _sharingDirty;
    private Task _sharingUpdate = Task.CompletedTask;

    [Reactive]
    public partial bool AudioForwardingEnabled { get; set; }
    [Reactive]
    public partial bool ClipboardSyncEnabled { get; set; }

    private void SharingPreferenceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(AudioForwardingEnabled) or nameof(ClipboardSyncEnabled) or nameof(NotificationSyncEnabled)))
        {
            return;
        }

        Dispatcher.UIThread.VerifyAccess();
        _settings.AudioForwardingEnabled = AudioForwardingEnabled;
        _settings.ClipboardSyncEnabled = ClipboardSyncEnabled;
        _settings.NotificationSyncEnabled = NotificationSyncEnabled;
        try { _settings.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Error = $"设置已生效，但保存失败：{ex.Message}"; }

        // Detach platform access immediately; the pump awaits unsubscribe before another subscription.
        if (!ClipboardSyncEnabled)
        {
            _clipboardSharing?.SetActiveClipboard(null);
        }

        if (!AudioForwardingEnabled)
        {
            _audioSharing?.StopPlayback();
        }

        if (!NotificationSyncEnabled) _notificationSharing?.Deactivate();

        _ = UpdateSharingAsync();
    }

    public void SetActiveMirror(MirrorViewModel mirror)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_disposed || !Mirrors.Contains(mirror) || mirror.IsDisposed)
        {
            return;
        }

        _activeMirror = mirror;
        _ = UpdateSharingAsync();
    }

    private Task UpdateSharingAsync()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_disposed || !IsConnected || !Mirrors.Any(static m => !m.IsDisposed && m.CanControl))
        {
            _audioSharing?.StopPlayback();
        }

        _sharingDirty = true;
        if (!_sharingUpdate.IsCompleted)
        {
            return _sharingUpdate;
        }

        return _sharingUpdate = ApplySharingAsync();
    }

    private async Task ApplySharingAsync()
    {
        do
        {
            _sharingDirty = false;
            try
            {
                if (_notificationSharing is { IsStopped: true })
                {
                    await StopNotificationsAsync().ConfigureAwait(true);
                }

                if (!_disposed && IsConnected && NotificationSyncEnabled)
                {
                    await StartNotificationsAsync().ConfigureAwait(true);
                }
                else
                {
                    await StopNotificationsAsync().ConfigureAwait(true);
                }

                if (!_disposed && IsConnected && ClipboardSyncEnabled)
                {
                    await StartClipboardAsync().ConfigureAwait(true);
                }
                else
                {
                    await StopClipboardAsync().ConfigureAwait(true);
                }

                if (_audioSharing is { IsStopped: true } stopped)
                {
                    _audioSharing = null;
                    await stopped.DisposeAsync().ConfigureAwait(true);
                    _sharingDirty = true;
                    continue;
                }

                AndromeldClient? client = _client;
                MirrorViewModel? mirror = _activeMirror is { IsDisposed: false, CanControl: true } active && Mirrors.Contains(active)
                    ? active
                    : Mirrors.FirstOrDefault(static m => !m.IsDisposed && m.CanControl);
                if (!_disposed && IsConnected && AudioForwardingEnabled && client is not null &&
                    client.Capabilities.Contains("audio") && client.Capabilities.Contains("device-audio") && mirror?.SessionId is { } sessionId)
                {
                    _audioSharing ??= new AudioSharing(client, ex => Error = $"音频转发失败：{ex.Message}", _connection.Token, _audioPlaybackFactory);
                    await _audioSharing.SetSessionAsync(sessionId).ConfigureAwait(true);
                }
                else
                {
                    AudioSharing? audio = _audioSharing;
                    _audioSharing = null;
                    if (audio is not null)
                    {
                        await audio.DisposeAsync().ConfigureAwait(true);
                    }
                }
            }
            catch (OperationCanceledException) when (_connection.IsCancellationRequested)
            {
                /* Disconnect owns cancellation. */
            }
            catch (Exception ex)
            {
                AudioSharing? audio = _audioSharing;
                _audioSharing = null;
                if (audio is not null)
                {
                    await audio.DisposeAsync().ConfigureAwait(true);
                }

                Error = $"同步服务失败：{ex.Message}";
            }
        } while (_sharingDirty);
    }

    private void MirrorSharingStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(MirrorViewModel.CanControl), StringComparison.Ordinal))
        {
            _ = UpdateSharingAsync();
        }
    }
}
