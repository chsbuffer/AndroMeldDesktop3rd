namespace Andromeld.Discovery;

public sealed class UsbConnectionException(UsbConnectionStage stage, Exception cause) : IOException(cause.Message, cause)
{
    public UsbConnectionStage Stage { get; } = stage;
}