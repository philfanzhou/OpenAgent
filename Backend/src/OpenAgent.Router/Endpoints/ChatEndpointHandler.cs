using System.Diagnostics;
using OpenAgent.Contracts.Security;
using OpenAgent.Router.Middleware;
using OpenAgent.Router.Models;
using OpenAgent.Router.Observability;
using Yarp.ReverseProxy.Forwarder;

namespace OpenAgent.Router.Endpoints;

internal static class ChatEndpointHandler
{
    internal static async Task<IResult> HandleAsync(
        string? action,
        HttpContext context,
        IHttpForwarder forwarder,
        IAgentUserContext userContext,
        ILogger logger,
        HttpMessageInvoker httpClient,
        ForwarderRequestConfig requestConfig,
        CancellationToken cancellationToken)
    {
        var traceId = Activity.Current?.Id ?? context.TraceIdentifier;
        if (userContext == null || !userContext.IsAuthenticated)
        {
            RouterLog.UnauthenticatedRequest(
                logger, action, context.Request.Method, context.Request.Path, traceId);
            return Results.Unauthorized();
        }

        var tenantId = context.Items[TenantIsolationMiddleware.TenantItemKey]?.ToString()
            ?? userContext.TenantId
            ?? context.Request.Headers["X-Tenant-Id"].FirstOrDefault();
        AgentRoutingFeature? routing = context.Features.Get<AgentRoutingFeature>();
        if (routing == null)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status500InternalServerError,
                title: "Agent routing was not resolved");
        }

        string? conversationId = routing.ConversationId;
        conversationId ??= context.Request.Headers["X-Conversation-Id"].FirstOrDefault();
        string targetEndpoint = routing.TargetEndpoint;

        RouterMeter.RecordRoute(action ?? "chat", "forwarding");
        string actionSuffix = string.IsNullOrWhiteSpace(action) ? string.Empty : $"/{action}";
        var targetUrl = $"{targetEndpoint.TrimEnd('/')}/api/v1/agent/chat{actionSuffix}";
        var currentRequestConfig = action is "sse" or "stream"
            ? new ForwarderRequestConfig { ActivityTimeout = Timeout.InfiniteTimeSpan }
            : requestConfig;
        var error = await forwarder.SendAsync(
            context,
            targetEndpoint,
            httpClient,
            currentRequestConfig,
            (_, proxyRequest) => ForwardingContextBuilder.ApplyAsync(
                proxyRequest, new Uri(targetUrl), userContext,
                tenantId, conversationId, traceId)).ConfigureAwait(false);
        return error == ForwarderError.None
            ? Results.Empty
            : await ForwardingErrorHandler.HandleChatAsync(
                context, action, error, targetEndpoint, targetUrl,
                userContext, tenantId, traceId, logger, cancellationToken).ConfigureAwait(false);
    }
}
