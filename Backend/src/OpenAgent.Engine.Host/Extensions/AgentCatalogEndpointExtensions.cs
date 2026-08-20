using Microsoft.AspNetCore.Mvc;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Abstract;
using OpenAgent.Core.Security;
using OpenAgent.Engine.Host.Middleware;

namespace OpenAgent.Engine.Host.Extensions;

internal static class AgentCatalogEndpointExtensions
{
    internal static void MapAgentCatalog(this RouteGroupBuilder group)
    {
        group.MapGet("/agents", ExecuteAsync)
            .WithName("ListAgents")
            .WithTags("Agent");
    }

    private static async Task<IResult> ExecuteAsync(
        [FromServices] IAgentConfigProvider configProvider,
        [FromServices] ILlmRegistry models,
        [FromServices] IAgentAuthorizationService authorization,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        IAgentUserContext user = context.GetAgentRequest().User;
        IReadOnlyList<AgentSummary> agents = await configProvider.ListAgentsAsync(
            AgentEndpointRequestMapper.RequireTenant(context),
            cancellationToken).ConfigureAwait(false);
        var result = new List<AgentSummary>(agents.Count);
        foreach (AgentSummary agent in agents)
        {
            result.Add(new AgentSummary
            {
                AgentId = agent.AgentId,
                Name = agent.Name,
                Description = agent.Description,
                Status = agent.Status,
                CurrentVersion = agent.CurrentVersion,
                ApiFormat = agent.ApiFormat,
                LlmProvider = agent.LlmProvider,
                LlmModel = agent.LlmModel,
                AvailableModels = await GetAvailableModelsAsync(
                    agent.AgentId,
                    models,
                    authorization,
                    user,
                    cancellationToken).ConfigureAwait(false)
            });
        }
        return Results.Ok(result);
    }

    private static async Task<IReadOnlyList<LlmModelOption>> GetAvailableModelsAsync(
        string agentId,
        ILlmRegistry models,
        IAgentAuthorizationService authorization,
        IAgentUserContext user,
        CancellationToken cancellationToken)
    {
        var result = new List<LlmModelOption>();
        foreach (LlmProviderProfile profile in models.GetAllProfiles()
            .Where(profile => profile.IsEnabled
                && string.Equals(profile.TenantId, user.TenantId, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(profile.Endpoint)
                && !string.IsNullOrWhiteSpace(profile.ApiKey)
                && !profile.ApiKey.StartsWith("***", StringComparison.Ordinal)))
        {
            foreach (string modelId in (profile.ModelIds ?? [])
                .Where(modelId => !string.IsNullOrWhiteSpace(modelId))
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                bool allowed = await authorization.IsAuthorizedAsync(
                    new AgentAuthorizationRequest(
                        agentId,
                        AgentResourceType.Model,
                        $"{profile.Id}/{modelId}",
                        "invoke"),
                    user,
                    cancellationToken).ConfigureAwait(false);
                if (allowed)
                {
                    result.Add(new LlmModelOption
                    {
                        Provider = profile.Id,
                        ProviderName = profile.Name,
                        ModelId = modelId
                    });
                }
            }
        }
        return result.AsReadOnly();
    }
}
