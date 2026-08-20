using OpenAgent.Contracts.Models;

namespace OpenAgent.Contracts.Configuration;

/// <summary>
/// Durable storage boundary for complete Agent configuration documents.
/// </summary>
public interface IAgentConfigRepository
{
    Task<AgentConfigEntity?> GetAsync(
        string agentId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgentConfigEntity>> ListAsync(
        string? tenantId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates or replaces an Agent configuration using optimistic concurrency.
    /// Returns <see langword="null"/> when the expected version is stale, the
    /// Agent belongs to another tenant, or a concurrent create wins the race.
    /// </summary>
    Task<AgentConfigEntity?> UpsertAsync(
        string agentId,
        AgentConfigEntity entity,
        string? expectedVersion,
        CancellationToken cancellationToken = default);
}
