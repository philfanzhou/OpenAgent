using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Core.Capabilities;

internal interface ICapabilitySource
{
    Task<IReadOnlyList<CapabilityDefinition>> DiscoverAsync(
        string agentId,
        AgentConfig config,
        IAgentUserContext user,
        CancellationToken cancellationToken);
}

internal sealed record CapabilityDefinition(
    string Name,
    string Description,
    string ParametersJsonSchema,
    AgentResourceType ResourceType,
    string ResourceId,
    Func<IReadOnlyDictionary<string, object?>, CancellationToken, Task<string>> Invoke,
    string? ParentResourceId = null,
    bool RequiresHumanApproval = false,
    string ApprovalAction = "invoke");
