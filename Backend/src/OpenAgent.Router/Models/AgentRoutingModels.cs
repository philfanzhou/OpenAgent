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

internal sealed record EngineRequestIdentity(
    string? Authorization,
    string? TenantId,
    string? AgentAudience);

internal sealed record AgentRoutingFeature(
    ParsedChatRequest Request,
    string? AgentId,
    string TargetEndpoint,
    bool SelectedByIntentAgent);

internal enum AgentSelectionStatus
{
    Selected,
    ContinueConversation,
    Forbidden,
    NoAgentAvailable
}

internal sealed record AgentSelectionRequest(
    string Query,
    string TargetEndpoint,
    string? ConversationId,
    string? ExplicitAgentId,
    EngineRequestIdentity Identity,
    IAgentUserContext UserContext,
    string? TraceId = null);

internal sealed record AgentSelectionResult(
    string? AgentId,
    bool SelectedByIntentAgent,
    double? Confidence,
    AgentSelectionStatus Status)
{
    internal bool IsSelected => Status == AgentSelectionStatus.Selected
        && !string.IsNullOrWhiteSpace(AgentId);

    internal bool CanForward => IsSelected
        || Status == AgentSelectionStatus.ContinueConversation;

    internal static AgentSelectionResult Selected(
        string agentId,
        bool selectedByIntentAgent = false,
        double? confidence = null) =>
        new(agentId, selectedByIntentAgent, confidence, AgentSelectionStatus.Selected);

    internal static AgentSelectionResult ContinueConversation() =>
        new(null, false, null, AgentSelectionStatus.ContinueConversation);

    internal static AgentSelectionResult Failed(AgentSelectionStatus status) =>
        new(null, false, null, status);
}

internal sealed record IntentAgentSelectionRequest(
    string Query,
    string TargetEndpoint,
    EngineRequestIdentity Identity,
    IAgentUserContext UserContext,
    IReadOnlyList<AgentSummary>? Candidates = null);
