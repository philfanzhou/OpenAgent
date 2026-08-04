using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Core.Capabilities.Rag;

internal sealed class RagCapabilitySource(RagSearchTool rag) : ICapabilitySource
{
    public Task<IReadOnlyList<CapabilityDefinition>> DiscoverAsync(
        string agentId,
        AgentConfig config,
        IAgentUserContext user,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<CapabilityDefinition> result = !config.Rag.Enabled
            ? []
            : [new CapabilityDefinition(
                rag.Name,
                rag.Description,
                rag.ParametersJsonSchema,
                AgentResourceType.Tool,
                rag.Name,
                (arguments, invocationCancellation) => rag.ExecuteAsync(
                    arguments.ToDictionary(item => item.Key, item => item.Value ?? string.Empty),
                    user,
                    config.Rag,
                    invocationCancellation))];
        return Task.FromResult(result);
    }
}
