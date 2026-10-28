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
    Task<string?> GetCachedResponseAsync(
        string query,
        CancellationToken cancellationToken = default);

    Task SetCachedResponseAsync(
        string query,
        string response,
        CancellationToken cancellationToken = default);

    async Task<CachedResponse?> GetAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        string? response = await GetCachedResponseAsync(
            key,
            cancellationToken).ConfigureAwait(false);
        return response == null
            ? null
            : new CachedResponse(
                StatusCodes.Status200OK,
                "application/json",
                System.Text.Encoding.UTF8.GetBytes(response));
    }

    async Task SetAsync(
        string key,
        CachedResponse response,
        TimeSpan timeToLive,
        CancellationToken cancellationToken = default)
    {
        await SetCachedResponseAsync(
            key,
            System.Text.Encoding.UTF8.GetString(response.Body),
            cancellationToken).ConfigureAwait(false);
    }
}
