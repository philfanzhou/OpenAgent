using OpenAgent.Contracts.Security;
using OpenAgent.Router.Observability;
using Yarp.ReverseProxy.Forwarder;

namespace OpenAgent.Router.Endpoints;

internal static class ForwardingErrorHandler
{
    internal static async Task<IResult> HandleChatAsync(
        HttpContext context,
        string? action,
        ForwarderError error,
        string targetEndpoint,
        string targetUrl,
        IAgentUserContext userContext,
        string? tenantId,
        string traceId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var exception = context.GetForwarderErrorFeature()?.Exception;
        RouterLog.ForwardingFailed(
            logger, exception, error, $"/api/v1/agent/chat/{action}", targetEndpoint,
            targetUrl, userContext.UserId, tenantId, traceId);
        RouterMeter.RecordDownstreamHealth("engine", "unavailable");
        RouterMeter.RecordForwardingFailure(action ?? "chat", error.ToString());
        return await StreamingForwardingError.HandleAsync(
            context, action, error, traceId, cancellationToken);
    }
}
