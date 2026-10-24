namespace Andromeld.Desktop.Services;

public sealed record FileDragSource(string Name, long Size, Func<Stream, CancellationToken, Task> Receive);