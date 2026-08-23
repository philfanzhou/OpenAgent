using OpenAgent.Contracts.Security;

namespace OpenAgent.Core.Capabilities;

internal sealed record ApprovalTarget(
    AgentResourceType ResourceType,
    string ResourceId,
    string Action);
