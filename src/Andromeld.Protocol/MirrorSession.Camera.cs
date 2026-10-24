using System.Net.WebSockets;

namespace Andromeld.Protocol;

public sealed partial class MirrorSession
{
    private int _cameraRequested;
    public bool CameraRequested => Volatile.Read(ref _cameraRequested) != 0;
    /// <summary>Media-reader notification; subscribers marshal to their presentation owner.</summary>
    public event Action<bool>? CameraRequestedChanged;

    /// <summary>One complete JPEG, only while Android has an active virtual camera stream.</summary>
    public Task<bool> SendCameraFrameAsync(ReadOnlyMemory<byte> jpeg, CancellationToken ct)
    {
        if (jpeg.Length is < 2 or > 1048576 || jpeg.Span[0] != 0xff || jpeg.Span[1] != 0xd8)
        {
            throw new ArgumentException("摄像头帧必须是 2 字节至 1 MiB 的 JPEG。", nameof(jpeg));
        }

        if (_disposed || State.IsPhone || !CameraRequested || !_attached.Task.IsCompletedSuccessfully)
        {
            return Task.FromResult(false);
        }

        return SendCameraCoreAsync(jpeg, ct);
    }

    private async Task<bool> SendCameraCoreAsync(ReadOnlyMemory<byte> jpeg, CancellationToken ct)
    {
        byte[] bytes = new byte[jpeg.Length + 2];
        bytes[0] = 7;
        bytes[1] = 1;
        jpeg.CopyTo(bytes.AsMemory(2));
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        await _send.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (_disposed || !CameraRequested)
            {
                return false;
            }

            await _socket.SendAsync(bytes, WebSocketMessageType.Binary, true, linked.Token).ConfigureAwait(false);
            return true;
        }
        finally { _send.Release(); }
    }

    private void SetCameraRequested(bool requested)
    {
        requested &= !_disposed;
        if (Interlocked.Exchange(ref _cameraRequested, requested ? 1 : 0) != (requested ? 1 : 0))
        {
            CameraRequestedChanged?.Invoke(requested);
        }
    }
}