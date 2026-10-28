namespace OpenAgent.Contracts.Configuration;

public interface IAgentConfigProvider
{
    Task<AgentConfig?> GetConfigAsync(
        string agentId,
        string tenantId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgentSummary>> ListAgentsAsync(
        string tenantId,
        CancellationToken cancellationToken = default);
}

public sealed class AgentSummary
{
    public string TenantId { get; init; } = string.Empty;
    public string AgentId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public int Status { get; init; }
    public string CurrentVersion { get; init; } = string.Empty;
}
