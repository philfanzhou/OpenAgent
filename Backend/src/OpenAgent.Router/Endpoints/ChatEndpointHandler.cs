using System.Diagnostics;
using System.Text.Json;
using OpenAgent.Contracts.Security;
using OpenAgent.Router.Middleware;
using OpenAgent.Router.Observability;
using OpenAgent.Router.Security;
using Yarp.ReverseProxy.Forwarder;

namespace OpenAgent.Router.Endpoints;

internal static class ChatEndpointHandler
{
    internal static async Task<IResult> HandleAsync(
        string? action,
        HttpContext context,
        IHttpForwarder forwarder,
        IAgentUserContext userContext,
        IIntentRecognizer intentRecognizer,
        IRouteTable routeTable,
        IAgentVisibilityService visibilityService,
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
        var (query, conversationId, bodyAgentId) = await ReadRequestAsync(
            context, action, traceId, logger).ConfigureAwait(false);
        conversationId ??= context.Request.Headers["X-Conversation-Id"].FirstOrDefault();
        RouterLog.RequestAccepted(
            logger, action, userContext.UserId, tenantId, conversationId, query, traceId);

        var agentId = bodyAgentId ?? context.Request.Headers["X-Agent-Id"].FirstOrDefault();
        var visibilityChecker = new AgentVisibilityChecker(intentRecognizer, visibilityService);
        var (intent, isVisible) = await visibilityChecker.CheckAsync(
            query, agentId, userContext, cancellationToken).ConfigureAwait(false);
        RouterLog.IntentRecognized(
            logger, action, intent, userContext.UserId, tenantId,
            conversationId, query.Length, traceId);
        if (!isVisible)
        {
            RouterLog.AgentAccessDenied(
                logger, action, userContext.UserId, tenantId, agentId!, conversationId, traceId);
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        var targetEndpoint = routeTable.GetTargetEndpoint(intent, tenantId, conversationId);
        if (string.IsNullOrEmpty(targetEndpoint))
        {
            RouterLog.TargetServiceNotFound(
                logger, action, intent, userContext.UserId, tenantId,
                agentId, conversationId, traceId);
            return Results.BadRequest(new { Error = "Unable to determine target service" });
        }

        RouterLog.ForwardingStarted(
            logger, action, targetEndpoint, intent, agentId, conversationId,
            userContext.UserId, tenantId, traceId);
        RouterMeter.RecordRoute(action ?? "chat", "forwarding");
        var targetUrl = $"{targetEndpoint.TrimEnd('/')}/api/v1/agent/chat/{action}";
        var currentRequestConfig = action is "sse" or "stream"
            ? new ForwarderRequestConfig { ActivityTimeout = Timeout.InfiniteTimeSpan }
            : requestConfig;
        var error = await forwarder.SendAsync(
            context,
            targetEndpoint,
            httpClient,
            currentRequestConfig,
            (_, proxyRequest) =>
            {
                RouterLog.ProxyRequestPrepared(
                    logger, action, targetUrl, agentId, conversationId,
                    userContext.UserId, tenantId, traceId);
                return ForwardingContextBuilder.ApplyAsync(
                    proxyRequest, new Uri(targetUrl), userContext,
                    tenantId, agentId, conversationId, traceId);
            }).ConfigureAwait(false);
        return error == ForwarderError.None
            ? Results.Empty
            : await ForwardingErrorHandler.HandleChatAsync(
                context, action, error, targetEndpoint, targetUrl,
                userContext, tenantId, traceId, logger, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<(string Query, string? ConversationId, string? AgentId)> ReadRequestAsync(
        HttpContext context,
        string? action,
        string traceId,
        ILogger logger)
    {
        context.Request.EnableBuffering();
        var body = string.Empty;
        try
        {
            using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
            body = await reader.ReadToEndAsync(CancellationToken.None).ConfigureAwait(false);
            context.Request.Body.Position = 0;
            return string.IsNullOrEmpty(body)
                ? (string.Empty, null, null)
                : ChatRequestParser.Parse(body);
        }
        catch (JsonException exception)
        {
            RouterLog.BodyNotValidJson(
                logger, exception, action, context.Request.Method,
                context.Request.Path, traceId, body.Length);
        }
        catch (Exception exception)
        {
            RouterLog.BodyReadFailed(
                logger, exception, action, context.Request.Method, context.Request.Path, traceId);
        }

        return (string.Empty, null, null);
    }
}
