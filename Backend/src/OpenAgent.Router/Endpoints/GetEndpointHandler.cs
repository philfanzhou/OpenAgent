using System.Diagnostics;
using OpenAgent.Contracts.Security;
using OpenAgent.Router.Observability;
using Yarp.ReverseProxy.Forwarder;

namespace OpenAgent.Router.Endpoints;

internal static class GetEndpointHandler
{
    internal static async Task<IResult> HandleAsync(
        HttpContext context,
        IHttpForwarder forwarder,
        IAgentUserContext userContext,
        IRouteTable routeTable,
        ILogger logger,
        HttpMessageInvoker httpClient,
        ForwarderRequestConfig requestConfig,
        string targetPath,
        string intent = "chat",
        bool conversationIdFromHeader = false)
    {
        if (userContext == null || !userContext.IsAuthenticated)
        {
            return Results.Unauthorized();
        }

        var tenantId = userContext.TenantId ?? context.Request.Headers["X-Tenant-Id"].FirstOrDefault();
        var conversationId = conversationIdFromHeader
            ? context.Request.Headers["X-Conversation-Id"].FirstOrDefault()
            : null;
        var targetEndpoint = routeTable.GetTargetEndpoint(intent, tenantId, conversationId);
        if (string.IsNullOrEmpty(targetEndpoint))
        {
            return Results.BadRequest(new { Error = "Unable to determine target service" });
        }

        var normalizedPath = targetPath.StartsWith('/') ? targetPath : "/" + targetPath;
        var targetUrl = $"{targetEndpoint.TrimEnd('/')}{normalizedPath}";
        var traceId = Activity.Current?.Id ?? context.TraceIdentifier;
        var error = await forwarder.SendAsync(
            context,
            targetEndpoint,
            httpClient,
            requestConfig,
            (_, proxyRequest) =>
            {
                proxyRequest.Method = HttpMethod.Get;
                return ForwardingContextBuilder.ApplyAsync(
                    proxyRequest, new Uri(targetUrl), userContext,
                    tenantId, conversationId, traceId);
            }).ConfigureAwait(false);
        if (error == ForwarderError.None)
        {
            return Results.Empty;
        }

        RouterLog.ForwardingFailed(
            logger, context.GetForwarderErrorFeature()?.Exception, error, targetPath,
            targetEndpoint, targetUrl, userContext.UserId, tenantId, traceId);
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
}
