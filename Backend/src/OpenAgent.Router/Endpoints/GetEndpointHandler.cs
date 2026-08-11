using System.Diagnostics;
using OpenAgent.Contracts.Security;
using OpenAgent.Hosting.Authorization;
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

        var tenantId = userContext.TenantId;
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
        ForwarderError error;
        try
        {
            error = await forwarder.SendAsync(
                context,
                targetEndpoint,
                httpClient,
                requestConfig,
                (_, proxyRequest) =>
                {
                    proxyRequest.Method = HttpMethod.Get;
                    return ForwardingContextBuilder.ApplyAsync(
                        proxyRequest,
                        new Uri(targetUrl),
                        traceId);
                }).ConfigureAwait(false);
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
            logger, context.GetForwarderErrorFeature()?.Exception, error, targetPath,
            targetEndpoint, targetUrl, userContext.UserId, tenantId, traceId);
        RouterMeter.RecordForwardingFailure("other", error.ToString());
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
}
