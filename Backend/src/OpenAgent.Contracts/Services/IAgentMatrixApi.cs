using OpenAgent.Contracts.Models;
using OpenAgent.Contracts.Security;
using OpenAgent.Contracts.Skills;

namespace OpenAgent.Contracts.Services;

/// <summary>
/// Defines the API for interacting with the Agent.Matrix backend.
/// </summary>
public interface IAgentMatrixApi
{
    /// <summary>
    /// Retrieves the published agent configuration entity for the given agent ID.
    /// </summary>
    Task<AgentConfigEntity?> GetAgentConfigAsync(string agentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves the list of available skills registered in the Matrix.
    /// </summary>
    Task<List<MatrixSkillMetadata>> GetSkillsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves the M2M authorization rules for a specific client.
    /// </summary>
    Task<List<M2MAuthRule>> GetM2MAuthRulesAsync(string clientId, CancellationToken cancellationToken = default);
}
