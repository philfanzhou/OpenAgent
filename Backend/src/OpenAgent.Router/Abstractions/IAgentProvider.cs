using OpenAgent.Contracts.Configuration;
using OpenAgent.Router.Models;

namespace OpenAgent.Router;

public interface IAgentProvider
{
    string Id { get; }

    Task<AgentProviderCatalog> GetAgentsAsync(
        AgentProviderRequestContext requestContext,
        CancellationToken cancellationToken = default);

    Task<AgentProviderConversationStatus> ResolveConversationAsync(
        AgentProviderRequestContext requestContext,
        string conversationId,
        CancellationToken cancellationToken = default);

    Task<IntentRecognitionResult?> RecognizeIntentAsync(
        string intentAgentId,
        IReadOnlyList<AgentSummary> agents,
        string message,
        CancellationToken cancellationToken = default);

    Task<AgentForwardingTarget?> ResolveForwardingAsync(
        string? action,
        string? tenantId,
        string? conversationId,
        CancellationToken cancellationToken = default);

    ValueTask ConfigureRequestAsync(
        HttpRequestMessage request,
        AgentForwardingTarget target,
        CancellationToken cancellationToken = default);
}
