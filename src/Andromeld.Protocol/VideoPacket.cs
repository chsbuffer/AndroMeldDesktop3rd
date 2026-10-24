namespace Andromeld.Protocol;

public sealed record VideoPacket(ulong AcknowledgedBytes, ulong TimestampUs, bool IsKeyFrame, ReadOnlyMemory<byte> Data);