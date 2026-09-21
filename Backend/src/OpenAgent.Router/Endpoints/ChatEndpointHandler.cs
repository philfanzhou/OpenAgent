using System.Diagnostics;
using OpenAgent.Contracts.Security;
using OpenAgent.Hosting.Errors;
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
            return TypedResults.Problem(AgentProblemDetails.AuthenticationRequired(
                "Authentication is required to chat.", context));
        }

        AgentRoutingFeature? routing = context.Features.Get<AgentRoutingFeature>();
        if (routing == null)
        {
            return TypedResults.Problem(AgentProblemDetails.Create(
                $"{AgentProblemDetails.TypePrefix}/agent-routing-unresolved",
                "AgentRoutingUnresolved",
                StatusCodes.Status500InternalServerError,
                "Agent routing was not resolved for this request.",
                context.Request.Path,
                AgentTraceIds.Resolve(context)));
        }

        if (!providers.TryGet(routing.ProviderId, out IAgentProvider? provider)
            || provider == null)
        {
            return TypedResults.Problem(RouterProblem.From(new AgentRoutingException(
                StatusCodes.Status503ServiceUnavailable,
                RouterErrorCodes.AgentProviderUnavailable,
                "Agent Provider is unavailable"), context));
        }

        await agentForwarder.ForwardAsync(
            context,
            provider,
            action,
            cancellationToken).ConfigureAwait(false);
        return Results.Empty;
    }
}
