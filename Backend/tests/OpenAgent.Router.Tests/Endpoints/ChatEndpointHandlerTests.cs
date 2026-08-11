using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Security;
using OpenAgent.Router.Endpoints;
using OpenAgent.Router.Models;
using Xunit;

namespace OpenAgent.Router.Tests.Endpoints;

public class ChatEndpointHandlerTests
{
    [Fact]
    public async Task HandleAsync_AnonymousRequest_ReturnsUnauthorized()
    {
        DefaultHttpContext context = CreateContext();
        var forwarder = new RecordingForwarder();

        IResult result = await HandleAsync(
            context,
            new StubRegistry(new StubProvider("self-engine")),
            forwarder,
            AnonymousUser);
        await result.ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.Equal(0, forwarder.CallCount);
    }

    [Fact]
    public async Task HandleAsync_RoutingWasNotResolved_ReturnsInternalServerError()
    {
        DefaultHttpContext context = CreateContext();

        IResult result = await HandleAsync(
            context,
            new StubRegistry(new StubProvider("self-engine")),
            new RecordingForwarder(),
            AuthenticatedUser);
        await result.ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
    }

    [Fact]
    public async Task HandleAsync_ProviderIsUnavailable_ReturnsServiceUnavailable()
    {
        DefaultHttpContext context = CreateContext();
        context.Features.Set(new AgentRoutingFeature("conversation-1", "missing"));

        IResult result = await HandleAsync(
            context,
            new StubRegistry(new StubProvider("self-engine")),
            new RecordingForwarder(),
            AuthenticatedUser);
        await result.ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
    }

    [Fact]
    public async Task HandleAsync_ResolvedProvider_ForwardsRequestedAction()
    {
        DefaultHttpContext context = CreateContext();
        context.Features.Set(new AgentRoutingFeature("conversation-1", "partner"));
        var provider = new StubProvider("partner");
        var forwarder = new RecordingForwarder();

        IResult result = await HandleAsync(
            context,
            new StubRegistry(provider),
            forwarder,
            AuthenticatedUser);

        Assert.NotNull(result);
        Assert.Equal(1, forwarder.CallCount);
        Assert.Same(context, forwarder.Context);
        Assert.Same(provider, forwarder.Provider);
        Assert.Equal("stream", forwarder.Action);
    }

    private static Task<IResult> HandleAsync(
        HttpContext context,
        IAgentProviderRegistry registry,
        IAgentForwarder forwarder,
        IAgentUserContext userContext) =>
        ChatEndpointHandler.HandleAsync(
            "stream",
            context,
            registry,
            forwarder,
            userContext,
            NullLogger.Instance,
            CancellationToken.None);

    private static DefaultHttpContext CreateContext() => new()
    {
        RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider()
    };

    private static AgentUserContext AuthenticatedUser => new()
    {
        UserId = "user-1",
        TenantId = "tenant-1",
        IsAuthenticated = true
    };

    private static AgentUserContext AnonymousUser => new()
    {
        UserId = string.Empty,
        IsAuthenticated = false
    };

    private sealed class StubRegistry(IAgentProvider? provider) : IAgentProviderRegistry
    {
        public IReadOnlyList<IAgentProvider> Providers => provider is null ? [] : [provider];

        public IAgentProvider DefaultProvider => provider
            ?? throw new InvalidOperationException("A default provider is required.");

        public bool TryGet(string providerId, out IAgentProvider? resolvedProvider)
        {
            resolvedProvider = provider?.Id == providerId ? provider : null;
            return resolvedProvider is not null;
        }
    }

    private sealed class RecordingForwarder : IAgentForwarder
    {
        public int CallCount { get; private set; }

        public HttpContext? Context { get; private set; }

        public IAgentProvider? Provider { get; private set; }

        public string? Action { get; private set; }

        public Task ForwardAsync(
            HttpContext context,
            IAgentProvider provider,
            string? action,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Context = context;
            Provider = provider;
            Action = action;
            return Task.CompletedTask;
        }
    }

    private sealed class StubProvider(string id) : IAgentProvider
    {
        public string Id => id;

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
            CancellationToken cancellationToken) =>
            Task.FromResult<AgentForwardingTarget?>(null);

        public ValueTask ConfigureRequestAsync(
            HttpRequestMessage request,
            AgentForwardingTarget target,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
