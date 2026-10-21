namespace OpenAgent.Router;

public interface IRouteTable
{
    string? GetTargetEndpoint(string intent);

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
    Task<RateLimitDecision> AcquireAsync(
        string clientId,
        CancellationToken cancellationToken = default);
}

public sealed record RateLimitDecision(
    bool IsAllowed,
    TimeSpan RetryAfter,
    bool IsDegraded,
    string Source);

internal interface IEndpointHealthTracker
{
    bool IsAvailable(string endpoint);
    void ReportSuccess(string endpoint);
    void ReportFailure(string endpoint);
}

internal interface IEngineReadinessProbe
{
    Task<bool> IsReadyAsync(string endpoint, CancellationToken cancellationToken = default);
}

public interface IQueryCache
{
    Task<CachedResponse?> GetAsync(
        string key,
        CancellationToken cancellationToken = default);

    Task SetAsync(
        string key,
        CachedResponse response,
        TimeSpan timeToLive,
        CancellationToken cancellationToken = default);
}
