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

    Task<IReadOnlyList<LlmProviderProfile>> ListAsync(
        string tenantId,
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
