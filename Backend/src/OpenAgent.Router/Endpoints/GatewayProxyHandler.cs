using System.Diagnostics;
using OpenAgent.Authorization;
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
        IPermissionAuthorizationService authorization = context.RequestServices
            .GetRequiredService<IPermissionAuthorizationService>();
        IDelegatedAuthorizationIssuer grantIssuer = context.RequestServices
            .GetRequiredService<IDelegatedAuthorizationIssuer>();
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
        ForwarderError error;
        try
        {
            error = await forwarder.SendAsync(
                context,
                targetEndpoint,
                httpClient,
                requestConfig,
                (_, proxyRequest) => userContext.IsAuthenticated
                    ? ApplyAuthenticatedAsync(
                        proxyRequest,
                        new Uri(targetUrl),
                        traceId)
                    : ApplyAnonymousAsync(proxyRequest, new Uri(targetUrl), traceId)).ConfigureAwait(false);
        }
        catch
        {
            RouterMeter.RecordForward("other", succeeded: false);
            throw;
        }
        RouterMeter.RecordForward("other", error == ForwarderError.None);
        if (error == ForwarderError.None)
        {
            context.RequestServices.GetService<IEndpointHealthTracker>()?.ReportSuccess(targetEndpoint);
            return Results.Empty;
        }

        context.RequestServices.GetService<IEndpointHealthTracker>()?.ReportFailure(targetEndpoint);
        RouterLog.DownstreamQuarantined(logger, targetEndpoint);

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
        RouterMeter.RecordForwardingFailure("other", error.ToString());
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    private static ValueTask ApplyAuthenticatedAsync(
        HttpRequestMessage proxyRequest,
        Uri targetUri,
        string traceId)
    {
        return ForwardingContextBuilder.ApplyAsync(
            proxyRequest,
            targetUri,
            traceId);
    }

    private static ValueTask ApplyAnonymousAsync(
        HttpRequestMessage proxyRequest,
        Uri targetUri,
        string traceId)
    {
        proxyRequest.RequestUri = targetUri;
        if (!proxyRequest.Headers.Contains("X-Trace-Id"))
        {
            proxyRequest.Headers.TryAddWithoutValidation("X-Trace-Id", traceId);
        }
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
                return PermissionCatalog.CapabilityTest;
            }

            if (request.Method == HttpMethods.Get)
            {
                return request.Path.Equals("/api/v1/admin/agents", StringComparison.OrdinalIgnoreCase)
                    ? PermissionCatalog.AgentRead
                    : PermissionCatalog.AgentConfigRead;
            }

            return PermissionCatalog.AgentConfigWrite;
        }

        if (request.Path.StartsWithSegments("/api/v1/agent/conversations"))
        {
            return request.Method == HttpMethods.Delete
                ? PermissionCatalog.ConversationDelete
                : PermissionCatalog.ConversationRead;
        }

        return request.Path.Equals("/api/v1/agent/me", StringComparison.OrdinalIgnoreCase)
            ? PermissionCatalog.IdentityRead
            : null;
    }
}
