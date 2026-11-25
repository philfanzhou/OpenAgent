using System.Diagnostics;
using OpenAgent.Contracts.Security;
using OpenAgent.Engine.Hosting;
using OpenAgent.Engine.Hosting.Errors;
using OpenAgent.Router.Models;
using OpenAgent.Router.Observability;
using OpenAgent.Router.Providers;
using Yarp.ReverseProxy.Forwarder;

namespace OpenAgent.Router.Endpoints;

internal sealed class AgentForwarder(
    IHttpForwarder forwarder,
    ILogger<AgentForwarder> logger,
    IEndpointHealthTracker healthTracker,
    IConfiguration configuration) : IAgentForwarder, IDisposable
{
    private static readonly ForwarderRequestConfig DefaultRequestConfig = new()
    {
        ActivityTimeout = TimeSpan.FromSeconds(100)
    };

    private readonly ForwarderRequestConfig _streamingRequestConfig = CreateStreamingRequestConfig(configuration);

    internal static ForwarderRequestConfig CreateStreamingRequestConfig(IConfiguration configuration) => new()
    {
        ActivityTimeout = TimeSpan.FromSeconds(Math.Max(16,
            configuration.GetValue("RouterSettings:Routing:StreamingIdleTimeoutSeconds", 120)))
    };

    private readonly HttpMessageInvoker _httpClient = ForwardingContextBuilder.CreateClient(configuration);

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
        string traceId = AgentProblemDetails.ResolveTraceId(context);

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
            await AgentProblemDetails.WriteAsync(
                context,
                RouterProblem.From(new AgentRoutingException(
                    StatusCodes.Status503ServiceUnavailable,
                    RouterErrorCodes.AgentProviderUnavailable,
                    "Agent Provider is unavailable"), context)).ConfigureAwait(false);
            return;
        }

        ForwarderRequestConfig requestConfig = isStreaming
            ? _streamingRequestConfig
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
                    traceId,
                    action,
                    logger,
                    cancellationToken)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            RouterLog.ProviderForwardingFailed(
                logger,
                exception,
                provider.Id,
                action,
                target.RequestUri.ToString(),
                traceId);
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
        RouterLog.ProviderForwardingResponse(
            logger,
            provider.Id,
            action ?? "chat",
            context.Response.StatusCode,
            RouterHttpLog.FormatResponseHeaders(context.Response.Headers),
            traceId);
        bool succeeded = error == ForwarderError.None;
        // 客户端主动断开 SSE（用户停止生成、页面关闭）表现为各类 *Canceled 且
        // RequestAborted 触发：这是正常结束，不应计为下游故障。
        if (!succeeded && IsClientCancellation(error) && context.RequestAborted.IsCancellationRequested)
        {
            return;
        }
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
            if (context.Response.StatusCode is 502 or 503 or 504)
                healthTracker.ReportFailure(target.DestinationPrefix);
            else
                healthTracker.ReportSuccess(target.DestinationPrefix);
            return;
        }

        healthTracker.ReportFailure(target.DestinationPrefix);
        if (!healthTracker.IsAvailable(target.DestinationPrefix))
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

    /// <summary>
    /// YARP 的取消类错误既可能来自客户端断开，也可能来自下游取消；
    /// 仅取消类错误 + RequestAborted 才判定为客户端主动断开。
    /// </summary>
    private static bool IsClientCancellation(ForwarderError error) =>
        error is ForwarderError.RequestCanceled
            or ForwarderError.RequestBodyCanceled
            or ForwarderError.ResponseBodyCanceled;

    private static async ValueTask ConfigureRequestAsync(
        HttpRequestMessage request,
        AgentForwardingTarget target,
        IAgentProvider provider,
        string traceId,
        string? action,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        await ForwardingContextBuilder.ApplyAsync(
            request,
            target.RequestUri,
            traceId,
            propagateBaggage: provider is OpenAgentEngineProvider).ConfigureAwait(false);
        await provider.ConfigureRequestAsync(
            request,
            target,
            cancellationToken).ConfigureAwait(false);
        RouterLog.ProviderForwardingRequest(
            logger,
            provider.Id,
            action ?? "chat",
            request.Method.Method,
            request.RequestUri?.ToString() ?? string.Empty,
            RouterHttpLog.FormatRequestHeaders(request),
            traceId);
    }

}
