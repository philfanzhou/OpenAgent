using System.Text.Json;
using OpenAgent.Contracts.Security;
using OpenAgent.Hosting.Authorization;
using OpenAgent.Router.Models;

namespace OpenAgent.Router.Endpoints;

internal sealed class AgentSelectionFilter(
    IAgentSelectionService selectionService,
    IAgentUserContext userContext,
    IGatewayAuthorizationService authorization) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext invocationContext,
        EndpointFilterDelegate next)
    {
        HttpContext context = invocationContext.HttpContext;
        RouterMeter.RecordRequest(context.Request.RouteValues["action"]?.ToString());
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
        AgentSelection? selection;
        try
        {
            selection = await selectionService.SelectAsync(
                request.Query,
                routingConversationId,
                explicitAgentId,
                context.RequestAborted,
                context.Request.Headers.Authorization.FirstOrDefault()).ConfigureAwait(false);
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

        if (!string.IsNullOrWhiteSpace(selection.AgentId)
            && !authorization.IsAuthorized(
                userContext,
                GatewayPermissions.AgentExecute,
                selection.AgentId))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        context.Features.Set(new AgentRoutingFeature(
            routingConversationId,
            selection.ProviderId,
            selection.AgentId));
        if (!string.IsNullOrWhiteSpace(selection.AgentId))
        {
            context.Request.Headers["X-Agent-Id"] = selection.AgentId;
            context.Response.Headers["X-OpenAgent-Selected-Agent-Id"] = selection.AgentId;
        }

        return await next(invocationContext).ConfigureAwait(false);
    }
}
