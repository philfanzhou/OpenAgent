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

internal enum AgentDestinationKind
{
    Engine,
    External
}

internal sealed record RoutableAgent(
    AgentSummary Summary,
    AgentDestinationKind DestinationKind,
    string TargetEndpoint);

internal sealed record AgentRoutingFeature(
    ParsedChatRequest Request,
    string AgentId,
    string TargetEndpoint,
    AgentDestinationKind DestinationKind,
    bool SelectedByIntentAgent);

internal sealed record DownstreamRequestIdentity(
    string? Authorization,
    string? TenantId,
    string? Audience,
    string? TraceId);

internal sealed record AgentCatalogRequest(
    string EngineEndpoint,
    DownstreamRequestIdentity Identity,
    IAgentUserContext UserContext,
    bool IntentCandidatesOnly);

internal sealed record IntentAgentSelectionRequest(
    string Query,
    string EngineEndpoint,
    DownstreamRequestIdentity Identity,
    IReadOnlyList<AgentSummary> Candidates);

internal sealed record ExternalForwardingResult(
    Yarp.ReverseProxy.Forwarder.ForwarderError Error,
    string TargetEndpoint,
    string TargetUrl);
