using OpenAgent.Contracts.Security;
using OpenAgent.Hosting.Errors;
using OpenAgent.Router.Models;

namespace OpenAgent.Router.Endpoints;

internal static class AgentCatalogEndpointHandler
{
    internal static async Task<IResult> HandleAsync(
        IAgentCatalogService catalog,
        IAgentUserContext userContext,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (!userContext.IsAuthenticated)
        {
            return TypedResults.Problem(AgentProblemDetails.AuthenticationRequired(
                "Authentication is required to list agents.", context));
        }

        if (string.IsNullOrWhiteSpace(userContext.TenantId))
        {
            return TypedResults.Problem(RouterProblem.From(new AgentRoutingException(
                StatusCodes.Status400BadRequest,
                RouterErrorCodes.InvalidTenant,
                "Tenant ID is required"), context));
        }

        try
        {
            IReadOnlyList<AgentCatalogEntry> entries = await catalog.GetAuthorizedAsync(
                new AgentProviderRequestContext(
                    userContext,
                    context.Request.Headers.Authorization.FirstOrDefault()),
                cancellationToken).ConfigureAwait(false);
            return TypedResults.Ok(entries.Select(entry => entry.Agent).ToList());
        }
        catch (AgentRoutingException exception)
        {
            return TypedResults.Problem(RouterProblem.From(exception, context));
        }
    }
}
