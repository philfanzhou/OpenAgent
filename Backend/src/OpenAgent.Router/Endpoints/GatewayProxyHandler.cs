using System.Diagnostics;
using OpenAgent.Contracts.Security;
using OpenAgent.Hosting.Authorization;
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
        IGatewayAuthorizationService authorization = context.RequestServices
            .GetRequiredService<IGatewayAuthorizationService>();
        if (requireAuthentication && !userContext.IsAuthenticated)
        {
            return Results.Unauthorized();
        }

        string? requiredPermission = ResolvePermission(context.Request);
        if (requiredPermission != null
            && !authorization.IsAuthorized(userContext, requiredPermission))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        string? tenantId = userContext.IsAuthenticated
            ? userContext.TenantId
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
        string? gatewayGrant = userContext.IsAuthenticated
            ? authorization.IssueGrant(userContext)
            : null;
        ForwarderError error = await forwarder.SendAsync(
            context,
            targetEndpoint,
            httpClient,
            requestConfig,
            (_, proxyRequest) => userContext.IsAuthenticated
                ? ForwardingContextBuilder.ApplyAsync(
                    proxyRequest,
                    new Uri(targetUrl),
                    userContext,
                    tenantId,
                    agentId: null,
                    conversationId,
                    traceId,
                    gatewayGrant!)
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

    private static ValueTask ApplyAnonymousAsync(
        HttpRequestMessage proxyRequest,
        Uri targetUri,
        string traceId)
    {
        proxyRequest.RequestUri = targetUri;
        proxyRequest.Headers.Remove("X-Agent-Id");
        proxyRequest.Headers.Remove(AgentRoutingHeaders.ResolvedAgentId);
        proxyRequest.Headers.Remove("X-Conversation-Id");
        proxyRequest.Headers.Remove("X-User-Id");
        proxyRequest.Headers.Remove("X-Tenant-Id");
        proxyRequest.Headers.Remove("X-Trace-Id");
        proxyRequest.Headers.Remove("Authorization");
        proxyRequest.Headers.Remove(GatewayAuthorizationDefaults.GrantHeaderName);
        proxyRequest.Headers.TryAddWithoutValidation("X-Trace-Id", traceId);
        return ValueTask.CompletedTask;
    }

    private static string? ResolvePermission(HttpRequest request)
    {
        if (request.Path.StartsWithSegments("/api/v1/admin"))
        {
            if (request.Method == HttpMethods.Post
                && (request.Path.Value?.EndsWith("/test", StringComparison.OrdinalIgnoreCase) == true
                    || request.Path.Value?.EndsWith("/test-connection", StringComparison.OrdinalIgnoreCase) == true))
            {
                return GatewayPermissions.CapabilityTest;
            }

            if (request.Method == HttpMethods.Get)
            {
                return request.Path.Equals("/api/v1/admin/agents", StringComparison.OrdinalIgnoreCase)
                    ? GatewayPermissions.AgentRead
                    : GatewayPermissions.AgentConfigRead;
            }

            return GatewayPermissions.AgentConfigWrite;
        }

        if (request.Path.StartsWithSegments("/api/v1/agent/conversations"))
        {
            return request.Method == HttpMethods.Delete
                ? GatewayPermissions.ConversationDelete
                : GatewayPermissions.ConversationRead;
        }

        return request.Path.Equals("/api/v1/agent/me", StringComparison.OrdinalIgnoreCase)
            ? GatewayPermissions.IdentityRead
            : null;
    }
}
