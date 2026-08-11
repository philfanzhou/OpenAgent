namespace OpenAgent.Router.Models;

internal sealed record ParsedChatRequest(
    string Query,
    string? ConversationId,
    string? AgentId);

internal sealed record AgentRoutingFeature(
    string? ConversationId,
    string ProviderId,
    string? AgentId);

internal sealed record AgentSelection(
    string? AgentId,
    string ProviderId);
