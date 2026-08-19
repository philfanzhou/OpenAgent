using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Router.Models;

public sealed record AgentProviderCatalog(
    IReadOnlyList<AgentSummary> Agents,
    bool IsAvailable = true);

public enum AgentProviderConversationStatus
{
    NotFound,
    Found,
    Forbidden,
    Unavailable
}

public sealed record AgentProviderRequestContext(
    string TenantId,
    IAgentUserContext UserContext);
