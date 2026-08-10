using OpenAgent.Contracts.Security;

namespace OpenAgent.Router.Models;

internal sealed record ParsedChatRequest(
    string Query,
    string? ConversationId,
    string? AgentId);

internal sealed record AgentRoutingFeature(
    string? ConversationId,
    string TargetEndpoint);

internal sealed record AgentSelectionRequest(
    string Query,
    string TargetEndpoint,
    string? ConversationId,
    string? ExplicitAgentId,
    string? Authorization,
    string? TenantId,
    IAgentUserContext UserContext);
