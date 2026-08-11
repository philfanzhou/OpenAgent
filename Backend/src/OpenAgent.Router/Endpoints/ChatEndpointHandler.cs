using System.Diagnostics;
using OpenAgent.Contracts.Security;
using OpenAgent.Router.Models;
using OpenAgent.Router.Observability;

namespace OpenAgent.Router.Endpoints;

internal static class ChatEndpointHandler
{
    internal static async Task<IResult> HandleAsync(
        string? action,
        HttpContext context,
        IAgentProviderRegistry providers,
        IAgentForwarder agentForwarder,
        IAgentUserContext userContext,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var traceId = Activity.Current?.Id ?? context.TraceIdentifier;
        if (userContext == null || !userContext.IsAuthenticated)
        {
            RouterLog.UnauthenticatedRequest(
                logger, action, context.Request.Method, context.Request.Path, traceId);
            return Results.Unauthorized();
        }

        AgentRoutingFeature? routing = context.Features.Get<AgentRoutingFeature>();
        if (routing == null)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status500InternalServerError,
                title: "Agent routing was not resolved");
        }

        if (!providers.TryGet(routing.ProviderId, out IAgentProvider? provider)
            || provider == null)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Agent provider is unavailable");
        }

        await agentForwarder.ForwardAsync(
            context,
            provider,
            action,
            cancellationToken).ConfigureAwait(false);
        return Results.Empty;
    }
}
