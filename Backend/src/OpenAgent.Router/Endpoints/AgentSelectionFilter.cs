using System.Text.Json;
using OpenAgent.Contracts.Security;
using OpenAgent.Router.Middleware;
using OpenAgent.Router.Models;

namespace OpenAgent.Router.Endpoints;

internal sealed class AgentSelectionFilter(
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

        string? routingConversationId = request.ConversationId
            ?? context.Request.Headers["X-Conversation-Id"].FirstOrDefault();
        string? explicitAgentId = string.IsNullOrWhiteSpace(request.AgentId)
            ? context.Request.Headers["X-Agent-Id"].FirstOrDefault()
            : request.AgentId;
        string? tenantId = context.Items[TenantIsolationMiddleware.TenantItemKey]?.ToString()
            ?? userContext.TenantId
            ?? context.Request.Headers["X-Tenant-Id"].FirstOrDefault();
        AgentSelection? selection;
        try
        {
            selection = await selectionService.SelectAsync(
                request.Query,
                tenantId ?? string.Empty,
                routingConversationId,
                explicitAgentId,
                context.RequestAborted).ConfigureAwait(false);
        }
        catch (AgentRoutingException exception)
        {
            return RouterProblem.From(exception);
        }
        if (selection == null)
        {
            return RouterProblem.From(new AgentRoutingException(
                StatusCodes.Status503ServiceUnavailable,
                RouterErrorCodes.NoAgentAvailable,
                "No Agent could be selected"));
        }

        context.Features.Set(new AgentRoutingFeature(
            routingConversationId,
            selection.ProviderId));
        if (!string.IsNullOrWhiteSpace(selection.AgentId))
        {
            context.Request.Headers["X-Agent-Id"] = selection.AgentId;
        }

        return await next(invocationContext).ConfigureAwait(false);
    }
}
