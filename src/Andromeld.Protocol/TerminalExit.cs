namespace Andromeld.Protocol;

public sealed record TerminalExit(int? Code, Exception? Error = null);