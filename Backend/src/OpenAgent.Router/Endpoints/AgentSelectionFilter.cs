using System.Text.Json;
using OpenAgent.Contracts.Security;
using OpenAgent.Router.Middleware;
using OpenAgent.Router.Models;
using OpenAgent.Router.Observability;

namespace OpenAgent.Router.Endpoints;

internal sealed class AgentSelectionFilter(
    IRouteTable routeTable,
    IAgentSelectionService selectionService,
    IAgentUserContext userContext,
    ILogger<AgentSelectionFilter> logger) : IEndpointFilter
{
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
        string? routingConversationId = request.ConversationId
            ?? context.Request.Headers["X-Conversation-Id"].FirstOrDefault();
        string? targetEndpoint = routeTable.GetTargetEndpoint(
            "chat",
            tenantId,
            routingConversationId);
        if (string.IsNullOrWhiteSpace(targetEndpoint))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "No Engine is available");
        }

        string? explicitAgentId = string.IsNullOrWhiteSpace(request.AgentId)
            ? context.Request.Headers["X-Agent-Id"].FirstOrDefault()
            : request.AgentId;
        var identity = new EngineRequestIdentity(
            context.Request.Headers.Authorization.FirstOrDefault(),
            context.Request.Headers["X-Tenant-Id"].FirstOrDefault(),
            context.Request.Headers["X-Agent-Audience"].FirstOrDefault());
        AgentSelectionResult selection = await selectionService.SelectAsync(
            new AgentSelectionRequest(
                request.Query,
                targetEndpoint,
                request.ConversationId,
                explicitAgentId,
                identity,
                userContext,
                context.TraceIdentifier),
            context.RequestAborted).ConfigureAwait(false);
        if (selection.Status == AgentSelectionStatus.Forbidden)
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        if (!selection.CanForward)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "No Agent could be selected");
        }

        context.Features.Set(new AgentRoutingFeature(
            request,
            selection.AgentId,
            targetEndpoint,
            selection.SelectedByIntentAgent));
        if (selection.IsSelected)
        {
            context.Request.Headers["X-Agent-Id"] = selection.AgentId!;
        }

        return await next(invocationContext).ConfigureAwait(false);
    }
}
