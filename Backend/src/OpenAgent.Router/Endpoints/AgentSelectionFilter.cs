using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Security;
using OpenAgent.Router.Middleware;
using OpenAgent.Router.Models;
using OpenAgent.Router.Observability;
using OpenAgent.Router.Options;
using OpenAgent.Router.Security;

namespace OpenAgent.Router.Endpoints;

internal sealed class AgentSelectionFilter(
    IRouteTable routeTable,
    IAgentVisibilityService visibilityService,
    IConversationAgentResolver conversationAgentResolver,
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

        string? explicitAgentId = string.IsNullOrWhiteSpace(request.AgentId)
            ? context.Request.Headers["X-Agent-Id"].FirstOrDefault()
            : request.AgentId;
        bool selectedByIntentAgent = false;
        string? selectedAgentId = explicitAgentId;
        if (string.IsNullOrWhiteSpace(selectedAgentId)
            && !string.IsNullOrWhiteSpace(conversationId))
        {
            try
            {
                ConversationAgentResolution resolution = await conversationAgentResolver.ResolveAsync(
                    targetEndpoint,
                    conversationId,
                    context,
                    context.RequestAborted).ConfigureAwait(false);
                if (resolution.Exists)
                {
                    selectedAgentId = resolution.AgentId;
                }
            }
            catch (HttpRequestException exception)
                when (exception.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
            catch (Exception exception) when (exception is HttpRequestException or JsonException)
            {
                RouterLog.ConversationAgentResolutionFailed(
                    logger,
                    exception,
                    conversationId,
                    context.TraceIdentifier);
                return Results.Problem(
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "Conversation routing is unavailable");
            }
        }

        if (string.IsNullOrWhiteSpace(selectedAgentId))
        {
            IntentAgentDecision? decision = _options.Enabled
                ? await selector.SelectAsync(
                    new IntentAgentSelectionRequest(
                        request.Query,
                        targetEndpoint,
                        context,
                        userContext),
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
            targetEndpoint,
            selectedByIntentAgent));
        context.Request.Headers["X-Agent-Id"] = selectedAgentId;
        return await next(invocationContext).ConfigureAwait(false);
    }
}
