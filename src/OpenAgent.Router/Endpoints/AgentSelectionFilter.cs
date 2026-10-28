using System.Text.Json;
using OpenAgent.Contracts.Security;
using OpenAgent.Hosting.Errors;
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
            return TypedResults.Problem(AgentProblemDetails.Invalid(
                "The request body must contain valid JSON.", context));
        }

        string? routingConversationId = request.ConversationId
            ?? context.Request.Headers["X-Conversation-Id"].FirstOrDefault();
        string? explicitAgentId = string.IsNullOrWhiteSpace(request.AgentId)
            ? context.Request.Headers["X-Agent-Id"].FirstOrDefault()
                ?? context.Request.Headers["X-Gina-Agent-Id"].FirstOrDefault()
            : request.AgentId;
        AgentSelection? selection;
        try
        {
            selection = await selectionService.SelectAsync(
                request.Query,
                routingConversationId,
                explicitAgentId,
                context.RequestAborted,
                context.Request.Headers.Authorization.FirstOrDefault(),
                request.LlmProfileId).ConfigureAwait(false);
        }
        catch (AgentRoutingException exception)
        {
            return TypedResults.Problem(RouterProblem.From(exception, context));
        }
        if (selection == null)
        {
            return TypedResults.Problem(RouterProblem.From(new AgentRoutingException(
                StatusCodes.Status503ServiceUnavailable,
                RouterErrorCodes.NoAgentAvailable,
                "No Agent could be selected"), context));
        }

        context.Features.Set(new AgentRoutingFeature(
            routingConversationId,
            selection.ProviderId));
        if (!string.IsNullOrWhiteSpace(selection.AgentId))
        {
            context.Request.Headers["X-Agent-Id"] = selection.AgentId;
            context.Response.Headers["X-OpenAgent-Selected-Agent-Id"] = selection.AgentId;
        }

        return await next(invocationContext).ConfigureAwait(false);
    }
}
