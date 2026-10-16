using OpenAgent.Contracts.Configuration;
using OpenAgent.Router.Models;

namespace OpenAgent.Router.Selection;

/// <summary>One way of choosing an Agent for a message that names none and has no conversation affinity.</summary>
internal interface IIntentStrategy
{
    /// <summary>Returns null when this strategy has no conclusion, so the next strategy is tried.</summary>
    Task<IntentSelection?> SelectAsync(
        AgentProviderRequestContext requestContext,
        string message,
        IReadOnlyList<AgentSummary> candidates,
        CancellationToken cancellationToken);
}

/// <param name="Source">Metric label of the strategy that decided, for example "intent" or "fallback".</param>
internal sealed record IntentSelection(string AgentId, string Source);
