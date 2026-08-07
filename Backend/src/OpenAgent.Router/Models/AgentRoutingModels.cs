using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Router.Models;

internal sealed record ParsedChatRequest(
    string Query,
    string? ConversationId,
    string? AgentId);

internal sealed record IntentAgentDecision(
    string AgentId,
    double Confidence,
    string? Reason);

internal sealed record AgentRoutingFeature(
    ParsedChatRequest Request,
    string AgentId,
    string TargetEndpoint,
    bool SelectedByIntentAgent);

internal sealed record IntentAgentSelectionRequest(
    string Query,
    string TargetEndpoint,
    HttpContext HttpContext,
    IAgentUserContext UserContext,
    IReadOnlyList<AgentSummary>? Candidates = null);
