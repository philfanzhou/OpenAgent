using System.Diagnostics;
using OpenAgent.Contracts.Security;
using OpenAgent.Router.Models;
using Yarp.ReverseProxy.Forwarder;

namespace OpenAgent.Router.Endpoints;

internal sealed class AgentForwarder(
    IHttpForwarder forwarder,
    ILogger<AgentForwarder> logger,
    IEndpointHealthTracker healthTracker) : IAgentForwarder, IDisposable
{
    private static readonly ForwarderRequestConfig DefaultRequestConfig = new()
    {
        ActivityTimeout = TimeSpan.FromSeconds(100)
    };

    private static readonly ForwarderRequestConfig StreamingRequestConfig = new()
    {
        ActivityTimeout = Timeout.InfiniteTimeSpan
    };

    private readonly HttpMessageInvoker _httpClient = new(CreateHandler());

    public async Task ForwardAsync(
        HttpContext context,
        IAgentProvider provider,
        string? action,
        CancellationToken cancellationToken)
    {
        IAgentUserContext userContext = context.RequestServices
            .GetRequiredService<IAgentUserContext>();
        string? tenantId = userContext.TenantId;
        string? conversationId = context.Features.Get<AgentRoutingFeature>()?.ConversationId
            ?? context.Request.Headers["X-Conversation-Id"].FirstOrDefault();
        string traceId = Activity.Current?.Id ?? context.TraceIdentifier;

        AgentForwardingTarget? target = await provider.ResolveForwardingAsync(
            action,
            tenantId,
            conversationId,
            cancellationToken).ConfigureAwait(false);
        if (target == null)
        {
            await RouterProblem.From(new AgentRoutingException(
                StatusCodes.Status503ServiceUnavailable,
                RouterErrorCodes.AgentProviderUnavailable,
                "Agent Provider is unavailable")).ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        ForwarderRequestConfig requestConfig = action is "sse" or "stream"
            ? StreamingRequestConfig
            : DefaultRequestConfig;
        ForwarderError error = await forwarder.SendAsync(
            context,
            target.DestinationPrefix,
            _httpClient,
            requestConfig,
            (_, proxyRequest) => ConfigureRequestAsync(
                proxyRequest,
                target,
                provider,
                tenantId,
                conversationId,
                traceId,
                cancellationToken)).ConfigureAwait(false);
        if (error == ForwarderError.None)
        {
            healthTracker.ReportSuccess(target.DestinationPrefix);
            return;
        }

        healthTracker.ReportFailure(target.DestinationPrefix);
        Observability.RouterLog.DownstreamQuarantined(logger, target.DestinationPrefix);

        IResult result = await ForwardingErrorHandler.HandleChatAsync(
            context,
            action,
            error,
            target.DestinationPrefix,
            target.RequestUri.ToString(),
            userContext,
            tenantId,
            traceId,
            logger,
            cancellationToken).ConfigureAwait(false);
        await result.ExecuteAsync(context).ConfigureAwait(false);
    }

    public void Dispose() => _httpClient.Dispose();

    private static async ValueTask ConfigureRequestAsync(
        HttpRequestMessage request,
        AgentForwardingTarget target,
        IAgentProvider provider,
        string? tenantId,
        string? conversationId,
        string traceId,
        CancellationToken cancellationToken)
    {
        await ForwardingContextBuilder.ApplyAsync(
            request,
            target.RequestUri,
            traceId).ConfigureAwait(false);
        await provider.ConfigureRequestAsync(
            request,
            target,
            cancellationToken).ConfigureAwait(false);
    }

    private static SocketsHttpHandler CreateHandler() => new()
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        AutomaticDecompression = System.Net.DecompressionMethods.None,
        UseCookies = false,
        EnableMultipleHttp2Connections = true,
        ActivityHeadersPropagator = DistributedContextPropagator.Current,
        ConnectTimeout = TimeSpan.FromSeconds(15)
    };
}
