using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Security;
using OpenAgent.Router.Endpoints;
using OpenAgent.Router.Models;
using Xunit;
using Yarp.ReverseProxy.Forwarder;

namespace OpenAgent.Router.Tests.Endpoints;

public class AgentForwarderTests
{
    [Fact]
    public async Task ForwardAsync_ProviderUnavailable_PassesRoutingValuesAndReturnsUnavailable()
    {
        var provider = new RecordingProvider();
        using ServiceProvider services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IAgentUserContext>(new AgentUserContext
            {
                UserId = "user-1",
                TenantId = "user-tenant",
                IsAuthenticated = true
            })
            .BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            RequestServices = services
        };
        context.Features.Set(new AgentRoutingFeature("conversation-1", provider.Id));
        using var forwarder = new AgentForwarder(
            null!,
            NullLogger<AgentForwarder>.Instance,
            new StubEndpointHealthTracker(),
            new ConfigurationBuilder().Build());

        await forwarder.ForwardAsync(
            context,
            provider,
            "stream",
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.Equal("stream", provider.Action);
        Assert.Equal("user-tenant", provider.TenantId);
        Assert.Equal("conversation-1", provider.ConversationId);
    }

    [Fact]
    public async Task ForwardAsync_ClientAbortedSse_DoesNotReportDownstreamFailure()
    {
        // 用户停止生成时客户端断开 SSE，YARP 返回取消类错误（如 ResponseBodyCanceled）：
        // 这是正常结束，计为下游故障会把 Engine 隔离 30 秒（FailureThreshold=1）。
        using var aborted = new CancellationTokenSource();
        aborted.Cancel();
        var provider = new RecordingProvider
        {
            Target = new AgentForwardingTarget(
                "http://localhost:5208",
                new Uri("http://localhost:5208/api/v1/agent/chat/stream"))
        };
        using ServiceProvider services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IAgentUserContext>(new AgentUserContext
            {
                UserId = "user-1",
                TenantId = "user-tenant",
                IsAuthenticated = true
            })
            .BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            RequestServices = services
        };
        context.RequestAborted = aborted.Token;
        context.Features.Set(new AgentRoutingFeature("conversation-1", provider.Id));
        var healthTracker = new RecordingEndpointHealthTracker();
        using var forwarder = new AgentForwarder(
            new FixedResultForwarder(ForwarderError.ResponseBodyCanceled),
            NullLogger<AgentForwarder>.Instance,
            healthTracker,
            new ConfigurationBuilder().Build());

        await forwarder.ForwardAsync(
            context,
            provider,
            "stream",
            CancellationToken.None);

        Assert.Empty(healthTracker.Failures);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    private sealed class FixedResultForwarder(ForwarderError result) : IHttpForwarder
    {
        public ValueTask<ForwarderError> SendAsync(
            HttpContext context,
            string destinationPrefix,
            HttpMessageInvoker httpClient,
            ForwarderRequestConfig requestConfig,
            [AllowNull] HttpTransformer transformer) => ValueTask.FromResult(result);
    }

    private sealed class RecordingProvider : IAgentProvider
    {
        public string Id => "partner";
        public string? Action { get; private set; }
        public string? TenantId { get; private set; }
        public string? ConversationId { get; private set; }
        public AgentForwardingTarget? Target { get; set; }

        public Task<AgentProviderCatalog> GetAgentsAsync(
            AgentProviderRequestContext requestContext,
            CancellationToken cancellationToken) =>
            Task.FromResult(new AgentProviderCatalog([]));

        public Task<AgentProviderConversationStatus> ResolveConversationAsync(
            AgentProviderRequestContext requestContext,
            string conversationId,
            CancellationToken cancellationToken) =>
            Task.FromResult(AgentProviderConversationStatus.NotFound);

        public Task<IntentRecognitionResult?> RecognizeIntentAsync(
            AgentProviderRequestContext requestContext,
            string intentAgentId,
            IReadOnlyList<AgentSummary> agents,
            string message,
            CancellationToken cancellationToken) =>
            Task.FromResult<IntentRecognitionResult?>(null);

        public Task<AgentForwardingTarget?> ResolveForwardingAsync(
            string? action,
            string? tenantId,
            string? conversationId,
            CancellationToken cancellationToken)
        {
            Action = action;
            TenantId = tenantId;
            ConversationId = conversationId;
            return Task.FromResult(Target);
        }

        public ValueTask ConfigureRequestAsync(
            HttpRequestMessage request,
            AgentForwardingTarget target,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class StubEndpointHealthTracker : IEndpointHealthTracker
    {
        public bool IsAvailable(string endpoint) => true;
        public void ReportSuccess(string endpoint) { }
        public void ReportFailure(string endpoint) { }
    }

    private sealed class RecordingEndpointHealthTracker : IEndpointHealthTracker
    {
        public List<string> Failures { get; } = [];

        public bool IsAvailable(string endpoint) => true;
        public void ReportSuccess(string endpoint) { }
        public void ReportFailure(string endpoint) => Failures.Add(endpoint);
    }
}
