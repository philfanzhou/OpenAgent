using System.Text.Json;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Router.Models;

namespace OpenAgent.Router.Routing;

internal sealed class AgentCatalogService(
    IAgentProviderRegistry providers,
    IEnumerable<IAgentAccessControl> accessControls) : IAgentCatalogService
{
    private readonly IReadOnlyList<IAgentAccessControl> _accessControls = accessControls.ToArray();

    public async Task<IReadOnlyList<AgentCatalogEntry>> GetAuthorizedAsync(
        AgentProviderRequestContext requestContext,
        CancellationToken cancellationToken)
    {
        List<AgentCatalogEntry> entries = [];
        bool hasAvailableProvider = false;
        foreach (IAgentProvider provider in providers.Providers)
        {
            AgentProviderCatalog catalog;
            try
            {
                catalog = await provider.GetAgentsAsync(
                    requestContext,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is HttpRequestException or JsonException)
            {
                // A failed provider must not hide agents published by healthy providers.
                continue;
            }

            if (!catalog.IsAvailable)
            {
                continue;
            }

            hasAvailableProvider = true;

            IReadOnlyList<AgentSummary> authorized = catalog.Agents
                .Where(agent => !string.IsNullOrWhiteSpace(agent.AgentId))
                .ToArray();
            foreach (IAgentAccessControl accessControl in _accessControls)
            {
                authorized = await accessControl.GetAuthorizedAgentsAsync(
                    requestContext.UserContext,
                    authorized,
                    cancellationToken).ConfigureAwait(false);
            }

            entries.AddRange(authorized.Select(agent => new AgentCatalogEntry(
                agent,
                provider.Id)));
        }

        if (!hasAvailableProvider)
        {
            throw ProviderUnavailable();
        }

        string? conflict = entries
            .GroupBy(entry => entry.Agent.AgentId, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1)
            ?.Key;
        if (conflict != null)
        {
            throw new AgentRoutingException(
                StatusCodes.Status409Conflict,
                RouterErrorCodes.AgentIdConflict,
                $"Agent ID '{conflict}' is not unique");
        }

        return entries
            .OrderBy(entry => entry.Agent.AgentId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<AgentCatalogEntry> ResolveAsync(
        AgentProviderRequestContext requestContext,
        string agentId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<AgentCatalogEntry> entries = await GetAuthorizedAsync(
            requestContext,
            cancellationToken).ConfigureAwait(false);
        AgentCatalogEntry? entry = entries.FirstOrDefault(candidate =>
            string.Equals(
                candidate.Agent.AgentId,
                agentId,
                StringComparison.OrdinalIgnoreCase));
        return entry ?? throw new AgentRoutingException(
            StatusCodes.Status404NotFound,
            RouterErrorCodes.AgentNotFound,
            "Agent was not found");
    }

    private static AgentRoutingException ProviderUnavailable() => new(
        StatusCodes.Status503ServiceUnavailable,
        RouterErrorCodes.AgentProviderUnavailable,
        "Agent catalog is temporarily unavailable");
}
