namespace Andromeld.Protocol;

/// <summary>Typed reply for fs-mkdir/fs-rename/fs-delete/fs-move/fs-send-to-session.</summary>
public sealed record FileOperationResult(string? C, string? ReqId, string? Op, bool Ok, string? Err);
