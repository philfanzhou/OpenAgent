namespace OpenAgent.Contracts.Security;

public enum AgentResourceType
{
    Agent,
    Model,
    Tool,
    Function,
    Mcp,
    Skill
}

public sealed record AgentAuthorizationRequest(
    string AgentId,
    AgentResourceType ResourceType,
    string ResourceId,
    string Action);
