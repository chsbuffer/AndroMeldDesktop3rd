namespace Andromeld.Cache;

public sealed record AppSnapshot(DateTimeOffset UpdatedAt, IReadOnlyList<CachedApp> Apps);