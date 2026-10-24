namespace Andromeld.Discovery;

public sealed record UsbConnectionProgress(
    UsbConnectionStage Stage,
    long BytesTransferred = 0,
    long? TotalBytes = null,
    UsbConnectionNotice? Notice = null);