using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Router.Models;
using OpenAgent.Router.Observability;
using OpenAgent.Router.Options;

namespace OpenAgent.Router.Endpoints;

internal sealed class IntentAgentSelector(
    IAgentProviderRegistry providers,
    IOptions<IntentRecognitionOptions> options,
    ILogger<IntentAgentSelector>? logger = null) : IIntentAgentSelector
{
    private readonly IntentRecognitionOptions _options = options.Value;
    private readonly ILogger<IntentAgentSelector> _logger = logger ?? NullLogger<IntentAgentSelector>.Instance;

    public async Task<string?> SelectAsync(
        AgentProviderRequestContext requestContext,
        string message,
        IReadOnlyList<AgentSummary> candidates,
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
                RouterLog.IntentRecognitionProviderUnavailable(
                    _logger,
                    _options.ProviderId,
                    _options.AgentId);
                return null;
            }

            RouterLog.IntentRecognitionStarted(
                _logger,
                provider.Id,
                _options.AgentId,
                candidates.Count,
                message.Length);
            IntentRecognitionResult? result = await provider.RecognizeIntentAsync(
                requestContext,
                _options.AgentId,
                candidates,
                message,
                timeout.Token).ConfigureAwait(false);
            string? selectedAgentId = ValidateResult(result, candidates, _options.MinimumConfidence);
            RouterLog.IntentRecognitionCompleted(
                _logger,
                provider.Id,
                _options.AgentId,
                result?.AgentId,
                result?.Confidence ?? -1,
                selectedAgentId);
            return selectedAgentId;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            RouterLog.IntentRecognitionTimedOut(_logger, _options.ProviderId, _options.AgentId);
            return null;
        }
        catch (HttpRequestException exception)
        {
            RouterLog.IntentRecognitionFailed(
                _logger,
                exception,
                _options.ProviderId,
                _options.AgentId);
            return null;
        }
        catch (Exception exception)
        {
            RouterLog.IntentRecognitionFailed(
                _logger,
                exception,
                _options.ProviderId,
                _options.AgentId);
            throw;
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
