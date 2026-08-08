using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Routing;
using OpenAgent.Contracts.Security;
using OpenAgent.Router.Middleware;
using OpenAgent.Router.Models;
using OpenAgent.Router.Observability;
using OpenAgent.Router.Options;
using OpenAgent.Router.Security;

namespace OpenAgent.Router.Endpoints;

internal sealed class AgentSelectionFilter(
    IRouteTable routeTable,
    IAgentCatalog catalog,
    IExternalAgentRegistry externalAgents,
    IAgentVisibilityService visibilityService,
    IIntentAgentSelector selector,
    IAgentUserContext userContext,
    IOptions<IntentRecognitionOptions> options,
    ILogger<AgentSelectionFilter> logger) : IEndpointFilter
{
    private readonly IntentRecognitionOptions _options = options.Value;

    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext invocationContext,
        EndpointFilterDelegate next)
    {
        HttpContext context = invocationContext.HttpContext;
        if (!userContext.IsAuthenticated)
        {
            return await next(invocationContext).ConfigureAwait(false);
        }

        ParsedChatRequest request;
        try
        {
            request = await ChatRequestReader.ReadAsync(
                context.Request,
                context.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            RouterLog.BodyNotValidJson(
                logger,
                exception,
                context.Request.RouteValues["action"]?.ToString(),
                context.Request.Method,
                context.Request.Path,
                context.TraceIdentifier,
                checked((int)Math.Min(context.Request.ContentLength ?? 0, int.MaxValue)));
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid chat request",
                detail: "The request body must contain valid JSON.");
        }
        catch (Exception exception) when (exception is InvalidDataException or BadHttpRequestException)
        {
            RouterLog.BodyReadFailed(
                logger,
                exception,
                context.Request.RouteValues["action"]?.ToString(),
                context.Request.Method,
                context.Request.Path,
                context.TraceIdentifier);
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid chat request",
                detail: "The request body could not be read.");
        }

        string? tenantId = context.Items[TenantIsolationMiddleware.TenantItemKey]?.ToString()
            ?? userContext.TenantId;
        string? conversationId = request.ConversationId
            ?? context.Request.Headers["X-Conversation-Id"].FirstOrDefault();
        string? targetEndpoint = routeTable.GetTargetEndpoint(
            "chat",
            tenantId,
            conversationId);
        if (string.IsNullOrWhiteSpace(targetEndpoint))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "No Engine is available");
        }

        DownstreamRequestIdentity identity = new(
            context.Request.Headers.Authorization.FirstOrDefault(),
            tenantId,
            context.Request.Headers["X-Agent-Audience"].FirstOrDefault(),
            context.Request.Headers["X-Trace-Id"].FirstOrDefault()
                ?? context.TraceIdentifier);
        string? explicitAgentId = string.IsNullOrWhiteSpace(request.AgentId)
            ? context.Request.Headers["X-Agent-Id"].FirstOrDefault()
            : request.AgentId;
        IReadOnlyList<RoutableAgent> candidates = string.IsNullOrWhiteSpace(explicitAgentId)
            ? await catalog.ListAsync(
                new AgentCatalogRequest(
                    targetEndpoint,
                    identity,
                    userContext,
                    IntentCandidatesOnly: true),
                context.RequestAborted).ConfigureAwait(false)
            : [];
        bool selectedByIntentAgent = false;
        string? selectedAgentId = explicitAgentId;
        if (string.IsNullOrWhiteSpace(selectedAgentId))
        {
            IntentAgentDecision? decision = _options.Enabled
                ? await selector.SelectAsync(
                    new IntentAgentSelectionRequest(
                        request.Query,
                        targetEndpoint,
                        identity,
                        candidates.Select(candidate => candidate.Summary).ToArray()),
                    context.RequestAborted).ConfigureAwait(false)
                : null;
            selectedAgentId = decision?.AgentId ?? _options.FallbackAgentId;
            selectedByIntentAgent = decision != null;
            RouterLog.AgentSelectionCompleted(
                logger,
                selectedAgentId,
                selectedByIntentAgent,
                decision?.Confidence,
                context.TraceIdentifier);
        }

        if (string.IsNullOrWhiteSpace(selectedAgentId))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "No Agent could be selected");
        }

        RoutableAgent? selectedAgent = candidates.FirstOrDefault(candidate =>
            string.Equals(
                candidate.Summary.AgentId,
                selectedAgentId,
                StringComparison.OrdinalIgnoreCase));
        AgentDestinationKind destinationKind = selectedAgent?.DestinationKind
            ?? AgentDestinationKind.Engine;
        string selectedTargetEndpoint = selectedAgent?.TargetEndpoint
            ?? targetEndpoint;
        if (selectedAgent == null
            && externalAgents.TryGet(selectedAgentId, out ExternalAgentOptions? externalAgent)
            && externalAgent != null)
        {
            destinationKind = AgentDestinationKind.External;
            selectedTargetEndpoint = externalAgent.BaseUrl.TrimEnd('/');
        }
        bool visible = await visibilityService.IsAgentVisibleToUserAsync(
            selectedAgentId,
            userContext,
            context.RequestAborted).ConfigureAwait(false);
        if (!visible)
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        context.Features.Set(new AgentRoutingFeature(
            request,
            selectedAgentId,
            selectedTargetEndpoint,
            destinationKind,
            selectedByIntentAgent));
        context.Response.Headers[AgentRoutingHeaders.SelectedAgentId] = selectedAgentId;
        return await next(invocationContext).ConfigureAwait(false);
    }
}
