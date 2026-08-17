using OpenAgent.Contracts.Security;

namespace OpenAgent.Router.Models;

public sealed record AgentProviderRequestContext(
    string TenantId,
    IAgentUserContext UserContext);
