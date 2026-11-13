namespace OpenAgent.Contracts.Configuration;

/// <summary>
/// Durable PostgreSQL storage boundary for tenant-scoped LLM profiles.
/// </summary>
public interface ILlmConfigRepository
{
    Task<LlmProviderProfile?> GetAsync(
        string tenantId,
        string profileId,
        CancellationToken cancellationToken = default);

    /// <summary>Lists one tenant's profiles, or all tenants for internal health aggregation when null.</summary>
    Task<IReadOnlyList<LlmProviderProfile>> ListAsync(
        string? tenantId = null,
        CancellationToken cancellationToken = default);

    Task<LlmProviderProfile> UpsertAsync(
        string tenantId,
        string profileId,
        LlmProviderProfile profile,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(
        string tenantId,
        string profileId,
        CancellationToken cancellationToken = default);
}
