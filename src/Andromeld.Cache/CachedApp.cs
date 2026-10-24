using Andromeld.Protocol;

namespace Andromeld.Cache;

public sealed record CachedApp(PhoneApp App, byte[]? Png, DateTimeOffset? IconUpdatedAt);