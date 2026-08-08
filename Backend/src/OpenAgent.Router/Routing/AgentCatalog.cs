using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Router.Models;
using OpenAgent.Router.Observability;
using OpenAgent.Router.Options;

namespace OpenAgent.Router.Routing;

internal sealed class AgentCatalog(
    IEngineAgentClient engineClient,
    IExternalAgentRegistry externalAgents,
    IAgentVisibilityService visibilityService,
    IMemoryCache memoryCache,
    IOptions<IntentRecognitionOptions> options,
    ILogger<AgentCatalog> logger) : IAgentCatalog
{
    private readonly IntentRecognitionOptions _options = options.Value;

    public async Task<IReadOnlyList<RoutableAgent>> ListAsync(
        AgentCatalogRequest request,
        CancellationToken cancellationToken)
    {
        List<RoutableAgent> catalog = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (AgentSummary summary in externalAgents.ListAgents())
        {
            if (externalAgents.TryGet(summary.AgentId, out ExternalAgentOptions? external)
                && external != null
                && seen.Add(summary.AgentId))
            {
                catalog.Add(new RoutableAgent(
                    summary,
                    AgentDestinationKind.External,
                    external.BaseUrl.TrimEnd('/')));
            }
        }

        IReadOnlyList<AgentSummary> engineAgents = await LoadEngineAgentsAsync(
            request,
            cancellationToken).ConfigureAwait(false);
        foreach (AgentSummary summary in engineAgents)
        {
            if (!string.IsNullOrWhiteSpace(summary.AgentId) && seen.Add(summary.AgentId))
            {
                catalog.Add(new RoutableAgent(
                    summary,
                    AgentDestinationKind.Engine,
                    request.EngineEndpoint.TrimEnd('/')));
            }
        }

        List<RoutableAgent> visible = [];
        foreach (RoutableAgent candidate in catalog.OrderBy(
            item => item.Summary.AgentId,
            StringComparer.OrdinalIgnoreCase))
        {
            if (request.IntentCandidatesOnly && string.Equals(
                candidate.Summary.AgentId,
                _options.AgentId,
                StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            bool allowed = await visibilityService.IsAgentVisibleToUserAsync(
                candidate.Summary.AgentId,
                request.UserContext,
                cancellationToken).ConfigureAwait(false);
            if (allowed)
            {
                visible.Add(candidate);
                if (request.IntentCandidatesOnly && visible.Count >= _options.MaxCandidates)
                {
                    break;
                }
            }
        }

        return visible;
    }

    private async Task<IReadOnlyList<AgentSummary>> LoadEngineAgentsAsync(
        AgentCatalogRequest request,
        CancellationToken cancellationToken)
    {
        string cacheKey = $"agent-catalog:{request.EngineEndpoint.TrimEnd('/')}";
        if (memoryCache.TryGetValue(cacheKey, out IReadOnlyList<AgentSummary>? cached)
            && cached != null)
        {
            return cached;
        }

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(_options.TimeoutMs));
        try
        {
            IReadOnlyList<AgentSummary> catalog = await engineClient.ListAgentsAsync(
                request.EngineEndpoint,
                request.Identity,
                timeout.Token).ConfigureAwait(false);
            memoryCache.Set(
                cacheKey,
                catalog,
                TimeSpan.FromSeconds(_options.CatalogCacheSeconds));
            return catalog;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            RouterLog.IntentRecognitionTimedOut(logger, _options.TimeoutMs);
            return [];
        }
        catch (HttpRequestException exception)
        {
            RouterLog.IntentRecognitionRequestFailed(logger, exception);
            return [];
        }
        catch (System.Text.Json.JsonException exception)
        {
            RouterLog.IntentRecognitionInvalidJson(logger, exception);
            return [];
        }
    }
}
