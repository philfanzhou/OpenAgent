using OpenAgent.Contracts.Security;

namespace OpenAgent.Contracts.Configuration;

/// <summary>
/// Resolves the configuration that is valid and authorized for one invocation.
/// Implementations may source raw definitions from Agent.Matrix, Redis, or tests.
/// </summary>
public interface IAgentRuntimeResolver
{
    Task<AgentRuntimeProfile> ResolveAsync(
        string agentId,
        IAgentUserContext userContext,
        CancellationToken cancellationToken = default);

    Task<AgentRuntimeProfile> ResolveAsync(
        string agentId,
        IAgentUserContext userContext,
        LlmModelSelection? modelOverride,
        CancellationToken cancellationToken = default) =>
        ResolveAsync(agentId, userContext, cancellationToken);
}
