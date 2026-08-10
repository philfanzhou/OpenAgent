using System.Text.Json;
using OpenAgent.Contracts.Security;
using OpenAgent.Router.Middleware;
using OpenAgent.Router.Models;

namespace OpenAgent.Router.Endpoints;

internal sealed class AgentSelectionFilter(
    IRouteTable routeTable,
    IAgentSelectionService selectionService,
    IAgentUserContext userContext) : IEndpointFilter
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
        catch (Exception exception) when (
            exception is JsonException or InvalidDataException or BadHttpRequestException)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid chat request",
                detail: "The request body must contain valid JSON.");
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
        string? selectedAgentId = await selectionService.SelectAsync(
            new AgentSelectionRequest(
                request.Query,
                targetEndpoint,
                request.ConversationId,
                explicitAgentId,
                context.Request.Headers.Authorization.FirstOrDefault(),
                tenantId,
                userContext),
            context.RequestAborted).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(selectedAgentId)
            && string.IsNullOrWhiteSpace(request.ConversationId))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "No Agent could be selected");
        }

        context.Features.Set(new AgentRoutingFeature(
            request.ConversationId,
            targetEndpoint));
        if (!string.IsNullOrWhiteSpace(selectedAgentId))
        {
            context.Request.Headers["X-Agent-Id"] = selectedAgentId;
        }

        return await next(invocationContext).ConfigureAwait(false);
    }
}
