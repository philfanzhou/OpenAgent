using System.Diagnostics;
using OpenAgent.Contracts.Security;
using OpenAgent.Router.Observability;
using Yarp.ReverseProxy.Forwarder;

namespace OpenAgent.Router.Endpoints;

internal static class GatewayProxyHandler
{
    internal static async Task<IResult> HandleAsync(
        HttpContext context,
        IHttpForwarder forwarder,
        IAgentUserContext userContext,
        IRouteTable routeTable,
        ILogger logger,
        HttpMessageInvoker httpClient,
        ForwarderRequestConfig requestConfig,
        bool requireAuthentication)
    {
        if (requireAuthentication && !userContext.IsAuthenticated)
        {
            return Results.Unauthorized();
        }

        string? tenantId = userContext.IsAuthenticated
            ? userContext.TenantId ?? context.Request.Headers["X-Tenant-Id"].FirstOrDefault()
            : null;
        string? conversationId = userContext.IsAuthenticated
            ? context.Request.RouteValues["conversationId"]?.ToString()
                ?? context.Request.Headers["X-Conversation-Id"].FirstOrDefault()
            : null;
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

        string targetUrl = $"{targetEndpoint.TrimEnd('/')}{context.Request.Path}{context.Request.QueryString}";
        string traceId = Activity.Current?.Id ?? context.TraceIdentifier;
        ForwarderError error = await forwarder.SendAsync(
            context,
            targetEndpoint,
            httpClient,
            requestConfig,
            (_, proxyRequest) => userContext.IsAuthenticated
                ? ApplyAuthenticatedAsync(
                    proxyRequest,
                    new Uri(targetUrl),
                    userContext,
                    tenantId,
                    conversationId,
                    traceId)
                : ApplyAnonymousAsync(proxyRequest, new Uri(targetUrl), traceId)).ConfigureAwait(false);
        if (error == ForwarderError.None)
        {
            return Results.Empty;
        }

        RouterLog.ForwardingFailed(
            logger,
            context.GetForwarderErrorFeature()?.Exception,
            error,
            context.Request.Path,
            targetEndpoint,
            targetUrl,
            userContext.UserId,
            tenantId,
            traceId);
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    private static ValueTask ApplyAuthenticatedAsync(
        HttpRequestMessage proxyRequest,
        Uri targetUri,
        IAgentUserContext userContext,
        string? tenantId,
        string? conversationId,
        string traceId)
    {
        proxyRequest.Headers.Remove("X-Agent-Id");
        return ForwardingContextBuilder.ApplyAsync(
            proxyRequest,
            targetUri,
            userContext,
            tenantId,
            conversationId,
            traceId);
    }

    private static ValueTask ApplyAnonymousAsync(
        HttpRequestMessage proxyRequest,
        Uri targetUri,
        string traceId)
    {
        proxyRequest.RequestUri = targetUri;
        proxyRequest.Headers.Remove("X-Agent-Id");
        proxyRequest.Headers.Remove("X-Conversation-Id");
        proxyRequest.Headers.Remove("X-User-Id");
        proxyRequest.Headers.Remove("X-Tenant-Id");
        proxyRequest.Headers.Remove("X-Trace-Id");
        proxyRequest.Headers.TryAddWithoutValidation("X-Trace-Id", traceId);
        return ValueTask.CompletedTask;
    }
}
