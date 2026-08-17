using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Security;
using OpenAgent.Router.Endpoints;
using OpenAgent.Router.Middleware;
using OpenAgent.Router.Models;
using Xunit;

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
        context.Items[TenantIsolationMiddleware.TenantItemKey] = "request-tenant";
        context.Features.Set(new AgentRoutingFeature("conversation-1", provider.Id));
        using var forwarder = new AgentForwarder(
            null!,
            NullLogger<AgentForwarder>.Instance,
            new StubEndpointHealthTracker());

        await forwarder.ForwardAsync(
            context,
            provider,
            "stream",
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.Equal("stream", provider.Action);
        Assert.Equal("request-tenant", provider.TenantId);
        Assert.Equal("conversation-1", provider.ConversationId);
    }

    private sealed class RecordingProvider : IAgentProvider
    {
        public string Id => "partner";
        public string? Action { get; private set; }
        public string? TenantId { get; private set; }
        public string? ConversationId { get; private set; }

        public Task<IReadOnlyList<AgentSummary>> GetAgentsAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AgentSummary>>([]);

        public Task<IntentRecognitionResult?> RecognizeIntentAsync(
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
            return Task.FromResult<AgentForwardingTarget?>(null);
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
}
