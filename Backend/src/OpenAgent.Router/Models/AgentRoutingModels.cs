using OpenAgent.Contracts.Configuration;

namespace OpenAgent.Router.Models;

internal sealed record AgentCatalogEntry(
    AgentSummary Agent,
    string ProviderId);

internal sealed record ParsedChatRequest(
    string Query,
    string? ConversationId,
    string? AgentId);

internal sealed record AgentRoutingFeature(
    string? ConversationId,
    string ProviderId);

internal sealed record AgentSelection(
    string? AgentId,
    string ProviderId);
