namespace OpenAgent.Router;

public interface IRouteTable
{
    string? GetTargetEndpoint(string intent);

    /// <summary>
    /// Get target endpoint with session affinity. When conversationId is provided,
    /// implementations should try to route to the same Engine for the same conversation.
    /// </summary>
    string? GetTargetEndpoint(string intent, string? conversationId)
    {
        return GetTargetEndpoint(intent, tenantId: null, conversationId);
    }

    /// <summary>
    /// Get target endpoint with session affinity and tenant isolation. When conversationId is provided,
    /// implementations should include tenantId in the affinity hash key to prevent cross-tenant collisions.
    /// </summary>
    string? GetTargetEndpoint(string intent, string? tenantId, string? conversationId)
    {
        return GetTargetEndpoint(intent);
    }
}

public interface IRateLimiter
{
    Task<bool> IsAllowedAsync(string clientId, CancellationToken cancellationToken = default);
}

public interface IIntentRecognizer
{
    Task<string> RecognizeAsync(string query, CancellationToken cancellationToken = default);
}

public interface IQueryCache
{
    Task<string?> GetCachedResponseAsync(string query, CancellationToken cancellationToken = default);
    Task SetCachedResponseAsync(string query, string response, CancellationToken cancellationToken = default);
}
