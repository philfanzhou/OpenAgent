using OpenAgent.Contracts.Security;

namespace OpenAgent.Contracts.Configuration;

public interface IAgentConfigProvider
{
    Task<AgentConfig> GetConfigAsync(CancellationToken cancellationToken = default);

    Task<AgentConfig?> GetConfigAsync(string agentId, CancellationToken cancellationToken = default);

    Task<AgentConfig?> GetConfigAsync(
        string agentId,
        string tenantId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgentSummary>> ListAgentsAsync(CancellationToken cancellationToken = default);

    /// <summary>Lists the Agents the user's tenant owns and the user may use.</summary>
    Task<IReadOnlyList<AgentSummary>> ListAgentsAsync(
        IAgentUserContext user,
        CancellationToken cancellationToken = default);
}

public sealed class AgentSummary
{
    public string AgentId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public int Status { get; init; }
    public string CurrentVersion { get; init; } = string.Empty;
    public string ApiFormat { get; init; } = string.Empty;
    public string LlmProvider { get; init; } = string.Empty;
    public string LlmModel { get; init; } = string.Empty;
}
