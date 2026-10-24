using Andromeld.Desktop.Services;
using Andromeld.Protocol;

using Avalonia.Input.Platform;

namespace Andromeld.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private IClipboard? _activeClipboard;
    private ClipboardSharing? _clipboardSharing;

    public void SetActiveClipboard(IClipboard? clipboard)
    {
        _activeClipboard = clipboard;
        _clipboardSharing?.SetActiveClipboard(ClipboardSyncEnabled ? clipboard : null);
    }

    private async Task StartClipboardAsync()
    {
        if (!ClipboardSyncEnabled || !IsConnected || _client is null || !_client.Capabilities.Contains("clipboard"))
        {
            return;
        }

        if (_clipboardSharing is { } existing)
        {
            existing.SetActiveClipboard(_activeClipboard);
            return;
        }

        AndromeldClient client = _client;
        ClipboardSharing sharing = new(client, ex => Error = $"剪贴板共享失败：{ex.Message}", _connection.Token);
        _clipboardSharing = sharing;
        try
        {
            await sharing.StartAsync();
            if (_clipboardSharing == sharing && _client == client && IsConnected && ClipboardSyncEnabled && !_disposed)
            {
                sharing.SetActiveClipboard(_activeClipboard);
            }
        }
        catch (Exception ex)
        {
            await StopClipboardAsync();
            if (ex is not OperationCanceledException)
            {
                Error = $"剪贴板共享失败：{ex.Message}";
            }
        }
    }

    private async Task StopClipboardAsync()
    {
        ClipboardSharing? sharing = _clipboardSharing;
        _clipboardSharing = null;
        if (sharing is not null)
        {
            await sharing.DisposeAsync();
        }
    }
}