namespace Andromeld.Protocol;

public sealed record AudioPacket(AudioConfiguration Configuration, ulong TimestampUs, ReadOnlyMemory<byte> Data);