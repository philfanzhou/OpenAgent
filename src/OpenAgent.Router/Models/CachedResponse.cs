namespace OpenAgent.Router;

public sealed record CachedResponse(int StatusCode, string? ContentType, byte[] Body);
