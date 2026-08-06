namespace OpenAgent.Contracts.Configuration;

public interface IAgentConfigProvider
{
    Task<AgentConfig> GetConfigAsync(CancellationToken cancellationToken = default);

    Task<AgentConfig?> GetConfigAsync(string agentId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgentSummary>> ListAgentsAsync(CancellationToken cancellationToken = default);
}

public sealed class AgentSummary
{
    public string AgentId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public int Status { get; init; }
    public string CurrentVersion { get; init; } = string.Empty;
    public string ApiFormat { get; init; } = string.Empty;
}
