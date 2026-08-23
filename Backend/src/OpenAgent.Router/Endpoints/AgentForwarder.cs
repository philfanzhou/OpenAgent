using System.Diagnostics;
using OpenAgent.Authorization;
using OpenAgent.Contracts.Security;
using OpenAgent.Router.Models;
using Yarp.ReverseProxy.Forwarder;

namespace OpenAgent.Router.Endpoints;

internal sealed class AgentForwarder(
    IHttpForwarder forwarder,
    IPermissionAuthorizationService authorization,
    IDelegatedAuthorizationIssuer grantIssuer,
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
        bool isStreaming = action is "sse" or "stream";
        long forwardingStarted = Stopwatch.GetTimestamp();
        IAgentUserContext userContext = context.RequestServices
            .GetRequiredService<IAgentUserContext>();
        string? tenantId = userContext.TenantId;
        string? conversationId = context.Features.Get<AgentRoutingFeature>()?.ConversationId
            ?? context.Request.Headers["X-Conversation-Id"].FirstOrDefault();
        string? agentId = context.Features.Get<AgentRoutingFeature>()?.AgentId;
        string traceId = Activity.Current?.Id ?? context.TraceIdentifier;
        string gatewayGrant = grantIssuer.Issue(
            authorization.CreateAgentDelegation(userContext, agentId));

        AgentForwardingTarget? target = await provider.ResolveForwardingAsync(
            action,
            tenantId,
            conversationId,
            cancellationToken).ConfigureAwait(false);
        if (target == null)
        {
            RouterMeter.RecordForward(action, succeeded: false);
            if (isStreaming)
            {
                RouterMeter.RecordSseCompletion(
                    action,
                    Stopwatch.GetElapsedTime(forwardingStarted),
                    succeeded: false);
            }
            await RouterProblem.From(new AgentRoutingException(
                StatusCodes.Status503ServiceUnavailable,
                RouterErrorCodes.AgentProviderUnavailable,
                "Agent Provider is unavailable")).ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        ForwarderRequestConfig requestConfig = isStreaming
            ? StreamingRequestConfig
            : DefaultRequestConfig;
        ForwarderError error;
        try
        {
            error = await forwarder.SendAsync(
                context,
                target.DestinationPrefix,
                _httpClient,
                requestConfig,
                (_, proxyRequest) => ConfigureRequestAsync(
                    proxyRequest,
                    target,
                    provider,
                    userContext,
                    tenantId,
                    agentId,
                    conversationId,
                    traceId,
                    gatewayGrant,
                    cancellationToken)).ConfigureAwait(false);
        }
        catch
        {
            RouterMeter.RecordForward(action, succeeded: false);
            if (isStreaming)
            {
                RouterMeter.RecordSseCompletion(
                    action,
                    Stopwatch.GetElapsedTime(forwardingStarted),
                    succeeded: false);
            }
            throw;
        }
        bool succeeded = error == ForwarderError.None;
        RouterMeter.RecordForward(action, succeeded);
        if (isStreaming)
        {
            RouterMeter.RecordSseCompletion(
                action,
                Stopwatch.GetElapsedTime(forwardingStarted),
                succeeded);
        }
        if (succeeded)
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
        IAgentUserContext userContext,
        string? tenantId,
        string? agentId,
        string? conversationId,
        string traceId,
        string gatewayGrant,
        CancellationToken cancellationToken)
    {
        await ForwardingContextBuilder.ApplyAsync(
            request,
            target.RequestUri,
            userContext,
            tenantId,
            agentId,
            conversationId,
            traceId,
            gatewayGrant).ConfigureAwait(false);
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
