namespace OpenAgent.Contracts.Configuration;

/// <summary>
/// Loads a tenant-owned LLM profile selected for one execution.
/// </summary>
public interface ILlmConfigProvider
{
    Task<LlmProviderProfile?> GetAsync(
        string tenantId,
        string profileId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LlmProviderProfile>> ListAsync(
        string tenantId,
        CancellationToken cancellationToken = default);
}
