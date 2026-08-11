using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Security;
using OpenAgent.Router.Models;
using OpenAgent.Router.Options;

namespace OpenAgent.Router.Endpoints;

internal sealed class IntentAgentSelector(
    IAgentProviderRegistry providers,
    IOptions<IntentRecognitionOptions> options) : IIntentAgentSelector
{
    private readonly IntentRecognitionOptions _options = options.Value;

    public async Task<string?> SelectAsync(
        string message,
        IReadOnlyList<AgentSummary> candidates,
        IAgentUserContext userContext,
        CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(_options.TimeoutMs));
        try
        {
            if (!providers.TryGet(_options.ProviderId, out IAgentProvider? provider)
                || provider == null)
            {
                return null;
            }

            IntentRecognitionResult? result = await provider.RecognizeIntentAsync(
                _options.AgentId,
                candidates,
                message,
                userContext,
                timeout.Token).ConfigureAwait(false);
            return ValidateResult(result, candidates, _options.MinimumConfidence);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    internal static string? ValidateResult(
        IntentRecognitionResult? result,
        IReadOnlyList<AgentSummary> candidates,
        double minimumConfidence)
    {
        if (result == null
            || string.IsNullOrWhiteSpace(result.AgentId)
            || result.Confidence < minimumConfidence
            || result.Confidence > 1)
        {
            return null;
        }

        AgentSummary? selected = candidates.FirstOrDefault(candidate =>
            string.Equals(candidate.AgentId, result.AgentId, StringComparison.OrdinalIgnoreCase));
        return selected?.AgentId;
    }
}
