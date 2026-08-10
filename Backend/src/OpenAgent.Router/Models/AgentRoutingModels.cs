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

internal readonly record struct ConversationAgentResolution(
    bool Exists,
    string? AgentId)
{
    internal static ConversationAgentResolution NotFound => new(false, null);
}

internal sealed record AgentRoutingFeature(
    ParsedChatRequest Request,
    string AgentId,
    string TargetEndpoint,
    bool SelectedByIntentAgent);

internal enum AgentSelectionFailure
{
    None,
    Forbidden,
    DependencyUnavailable,
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
    AgentSelectionFailure Failure)
{
    internal bool IsSelected => Failure == AgentSelectionFailure.None
        && !string.IsNullOrWhiteSpace(AgentId);

    internal static AgentSelectionResult Selected(
        string agentId,
        bool selectedByIntentAgent = false,
        double? confidence = null) =>
        new(agentId, selectedByIntentAgent, confidence, AgentSelectionFailure.None);

    internal static AgentSelectionResult Failed(AgentSelectionFailure failure) =>
        new(null, false, null, failure);
}

internal sealed record IntentAgentSelectionRequest(
    string Query,
    string TargetEndpoint,
    EngineRequestIdentity Identity,
    IAgentUserContext UserContext,
    IReadOnlyList<AgentSummary>? Candidates = null);
