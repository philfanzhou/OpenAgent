using OpenAgent.Contracts.Security;
using OpenAgent.Router.Models;

namespace OpenAgent.Router.Endpoints;

internal static class AgentCatalogEndpointHandler
{
    internal static async Task<IResult> HandleAsync(
        IAgentCatalogService catalog,
        IAgentUserContext userContext,
        CancellationToken cancellationToken)
    {
        if (!userContext.IsAuthenticated)
        {
            return Results.Unauthorized();
        }

        if (string.IsNullOrWhiteSpace(userContext.TenantId))
        {
            return RouterProblem.From(new AgentRoutingException(
                StatusCodes.Status400BadRequest,
                RouterErrorCodes.InvalidTenant,
                "Tenant ID is required"));
        }

        try
        {
            IReadOnlyList<AgentCatalogEntry> entries = await catalog.GetAuthorizedAsync(
                new AgentProviderRequestContext(userContext.TenantId, userContext),
                cancellationToken).ConfigureAwait(false);
            return Results.Ok(entries.Select(entry => entry.Agent));
        }
        catch (AgentRoutingException exception)
        {
            return RouterProblem.From(exception);
        }
    }
}
