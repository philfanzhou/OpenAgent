using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Router.Models;
using OpenAgent.Router.Options;

namespace OpenAgent.Router.Selection;

/// <summary>Selects the configured default Agent when it is among the candidates.</summary>
internal sealed class FallbackStrategy(IOptions<IntentRecognitionOptions> options) : IIntentStrategy
{
    public const string SourceName = "fallback";

    public Task<IntentSelection?> SelectAsync(
        AgentProviderRequestContext requestContext,
        string message,
        IReadOnlyList<AgentSummary> candidates,
        CancellationToken cancellationToken)
    {
        string fallbackAgentId = options.Value.FallbackAgentId;
        AgentSummary? fallback = string.IsNullOrWhiteSpace(fallbackAgentId)
            ? null
            : candidates.FirstOrDefault(candidate => string.Equals(
                candidate.AgentId,
                fallbackAgentId,
                StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(
            fallback == null ? null : new IntentSelection(fallback.AgentId, SourceName));
    }
}
