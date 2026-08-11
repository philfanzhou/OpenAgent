using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Security;
using OpenAgent.Router.Models;
using OpenAgent.Router.Options;

namespace OpenAgent.Router.Endpoints;

internal sealed class AgentSelectionService(
    IAgentProviderRegistry providers,
    IEnumerable<IAgentAccessControl> accessControls,
    IIntentAgentSelector intentAgentSelector,
    IAgentUserContext userContext,
    IOptions<IntentRecognitionOptions> options) : IAgentSelectionService
{
    private readonly IReadOnlyList<IAgentAccessControl> _accessControls = accessControls.ToArray();
    private readonly IntentRecognitionOptions _options = options.Value;

    public async Task<AgentSelection?> SelectAsync(
        string message,
        string? conversationId,
        string? explicitAgentId,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(explicitAgentId))
        {
            return new AgentSelection(explicitAgentId, providers.DefaultProvider.Id);
        }

        if (!string.IsNullOrWhiteSpace(conversationId))
        {
            return new AgentSelection(null, providers.DefaultProvider.Id);
        }

        if (!_options.Enabled)
        {
            return CreateFallbackSelection(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        }

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(_options.TimeoutMs));
        try
        {
            IReadOnlyDictionary<string, string> providerByAgent;
            IReadOnlyList<AgentSummary> candidates;
            (candidates, providerByAgent) = await LoadCandidatesAsync(
                timeout.Token).ConfigureAwait(false);
            string? selectedAgentId = await intentAgentSelector.SelectAsync(
                message,
                candidates,
                timeout.Token).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(selectedAgentId)
                && providerByAgent.TryGetValue(selectedAgentId, out string? providerId))
            {
                return new AgentSelection(selectedAgentId, providerId);
            }

            return CreateFallbackSelection(providerByAgent);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return CreateFallbackSelection(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        }
    }

    private async Task<(
        IReadOnlyList<AgentSummary> Agents,
        IReadOnlyDictionary<string, string> ProviderByAgent)> LoadCandidatesAsync(
        CancellationToken cancellationToken)
    {
        List<AgentSummary> candidates = [];
        Dictionary<string, string> providerByAgent = new(StringComparer.OrdinalIgnoreCase);
        foreach (IAgentProvider provider in providers.Providers)
        {
            IReadOnlyList<AgentSummary> providerAgents;
            try
            {
                providerAgents = await provider.GetAgentsAsync(
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is HttpRequestException or JsonException)
            {
                continue;
            }

            foreach (AgentSummary agent in providerAgents)
            {
                if (string.IsNullOrWhiteSpace(agent.AgentId)
                    || string.Equals(
                        agent.AgentId,
                        _options.AgentId,
                        StringComparison.OrdinalIgnoreCase)
                    || !providerByAgent.TryAdd(agent.AgentId, provider.Id))
                {
                    continue;
                }

                candidates.Add(agent);
            }
        }

        IReadOnlyList<AgentSummary> authorized = candidates;
        foreach (IAgentAccessControl accessControl in _accessControls)
        {
            authorized = await accessControl.GetAuthorizedAgentsAsync(
                userContext,
                authorized,
                cancellationToken).ConfigureAwait(false);
        }

        HashSet<string> authorizedIds = authorized
            .Select(agent => agent.AgentId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> authorizedProviders = providerByAgent
            .Where(pair => authorizedIds.Contains(pair.Key))
            .ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.OrdinalIgnoreCase);
        return (
            authorized
                .Where(agent => authorizedProviders.ContainsKey(agent.AgentId))
                .OrderBy(agent => agent.AgentId, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            authorizedProviders);
    }

    private AgentSelection? CreateFallbackSelection(
        IReadOnlyDictionary<string, string> providerByAgent)
    {
        if (string.IsNullOrWhiteSpace(_options.FallbackAgentId))
        {
            return null;
        }

        string providerId = providerByAgent.TryGetValue(
            _options.FallbackAgentId,
            out string? candidateProviderId)
            ? candidateProviderId
            : providers.DefaultProvider.Id;
        return new AgentSelection(_options.FallbackAgentId, providerId);
    }
}
