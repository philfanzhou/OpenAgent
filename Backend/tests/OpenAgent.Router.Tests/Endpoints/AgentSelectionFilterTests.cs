using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Routing;
using OpenAgent.Contracts.Security;
using OpenAgent.Router.Endpoints;
using OpenAgent.Router.Models;
using OpenAgent.Router.Options;
using OpenAgent.Router.Security;
using Xunit;

namespace OpenAgent.Router.Tests.Endpoints;

public class AgentSelectionFilterTests
{
    [Fact]
    public async Task InvokeAsync_NoExplicitAgent_UsesIntentAgentSelection()
    {
        var selector = new StubSelector(new IntentAgentDecision("finance", 0.9, null));
        var authorization = new TestGatewayAuthorizationService();
        AgentSelectionFilter filter = CreateFilter(selector, authorization: authorization);
        DefaultHttpContext httpContext = CreateHttpContext("{\"message\":\"find invoice\"}");
        var invocation = new DefaultEndpointFilterInvocationContext(httpContext);

        object? result = await filter.InvokeAsync(
            invocation,
            _ => ValueTask.FromResult<object?>(Results.Ok()));

        Assert.NotNull(result);
        AgentRoutingFeature? feature = httpContext.Features.Get<AgentRoutingFeature>();
        Assert.NotNull(feature);
        Assert.Equal("finance", feature.AgentId);
        Assert.Equal(AgentDestinationKind.Engine, feature.DestinationKind);
        Assert.True(feature.SelectedByIntentAgent);
        Assert.Equal(1, selector.CallCount);
        Assert.Equal(0, httpContext.Request.Body.Position);
        Assert.Equal("finance", httpContext.Response.Headers[AgentRoutingHeaders.SelectedAgentId]);
        Assert.Equal(
            ["agent.execute:intent-router", GatewayPermissions.ModelInvoke],
            authorization.RestrictedPermissions);
    }

    [Fact]
    public async Task InvokeAsync_IntentSelectsExternalAgent_UsesExternalDestination()
    {
        var selector = new StubSelector(new IntentAgentDecision("external-support", 0.9, null));
        AgentSelectionFilter filter = CreateFilter(
            selector,
            [
                new RoutableAgent(
                    new OpenAgent.Contracts.Configuration.AgentSummary
                    {
                        AgentId = "external-support",
                        Name = "Partner Support"
                    },
                    AgentDestinationKind.External,
                    "https://partner.example")
            ]);
        DefaultHttpContext httpContext = CreateHttpContext("{\"message\":\"hello\"}");
        var invocation = new DefaultEndpointFilterInvocationContext(httpContext);

        await filter.InvokeAsync(
            invocation,
            _ => ValueTask.FromResult<object?>(Results.Ok()));

        AgentRoutingFeature? feature = httpContext.Features.Get<AgentRoutingFeature>();
        Assert.NotNull(feature);
        Assert.Equal("external-support", feature.AgentId);
        Assert.Equal(AgentDestinationKind.External, feature.DestinationKind);
        Assert.Equal("https://partner.example", feature.TargetEndpoint);
    }

    [Fact]
    public async Task InvokeAsync_ExplicitExternalAgent_BypassesIntentAndUsesRegistry()
    {
        var selector = new StubSelector(new IntentAgentDecision("finance", 0.9, null));
        IReadOnlyList<RoutableAgent> agents =
        [
            new RoutableAgent(
                new OpenAgent.Contracts.Configuration.AgentSummary
                {
                    AgentId = "external-support",
                    Name = "Partner Support"
                },
                AgentDestinationKind.External,
                "https://partner.example")
        ];
        AgentSelectionFilter filter = CreateFilter(selector, agents);
        DefaultHttpContext httpContext = CreateHttpContext(
            "{\"message\":\"hello\",\"context\":{\"agentId\":\"external-support\"}}");
        var invocation = new DefaultEndpointFilterInvocationContext(httpContext);

        await filter.InvokeAsync(
            invocation,
            _ => ValueTask.FromResult<object?>(Results.Ok()));

        AgentRoutingFeature? feature = httpContext.Features.Get<AgentRoutingFeature>();
        Assert.NotNull(feature);
        Assert.Equal(AgentDestinationKind.External, feature.DestinationKind);
        Assert.Equal("https://partner.example", feature.TargetEndpoint);
        Assert.Equal(0, selector.CallCount);
    }

    [Fact]
    public async Task InvokeAsync_ExplicitAgent_BypassesIntentAgentSelection()
    {
        var selector = new StubSelector(new IntentAgentDecision("finance", 0.9, null));
        AgentSelectionFilter filter = CreateFilter(selector);
        DefaultHttpContext httpContext = CreateHttpContext(
            "{\"message\":\"hello\",\"context\":{\"agentId\":\"support\"}}");
        var invocation = new DefaultEndpointFilterInvocationContext(httpContext);

        await filter.InvokeAsync(
            invocation,
            _ => ValueTask.FromResult<object?>(Results.Ok()));

        AgentRoutingFeature? feature = httpContext.Features.Get<AgentRoutingFeature>();
        Assert.NotNull(feature);
        Assert.Equal("support", feature.AgentId);
        Assert.False(feature.SelectedByIntentAgent);
        Assert.Equal(0, selector.CallCount);
    }

    [Fact]
    public async Task InvokeAsync_IntentAgentUnavailable_UsesConfiguredFallback()
    {
        var selector = new StubSelector(null);
        AgentSelectionFilter filter = CreateFilter(selector);
        DefaultHttpContext httpContext = CreateHttpContext("{\"message\":\"hello\"}");
        var invocation = new DefaultEndpointFilterInvocationContext(httpContext);

        await filter.InvokeAsync(
            invocation,
            _ => ValueTask.FromResult<object?>(Results.Ok()));

        AgentRoutingFeature? feature = httpContext.Features.Get<AgentRoutingFeature>();
        Assert.NotNull(feature);
        Assert.Equal("default", feature.AgentId);
        Assert.False(feature.SelectedByIntentAgent);
        Assert.Equal(1, selector.CallCount);
    }

    [Fact]
    public async Task InvokeAsync_ExplicitUnauthorizedAgent_ReturnsForbiddenBeforeForwarding()
    {
        var selector = new StubSelector(null);
        AgentSelectionFilter filter = CreateFilter(
            selector,
            authorization: new TestGatewayAuthorizationService(
                evaluator: (_, resourceId) => resourceId != "support"));
        DefaultHttpContext httpContext = CreateHttpContext(
            "{\"message\":\"hello\",\"context\":{\"agentId\":\"support\"}}");
        var invocation = new DefaultEndpointFilterInvocationContext(httpContext);

        object? result = await filter.InvokeAsync(
            invocation,
            _ => ValueTask.FromResult<object?>(Results.Ok()));
        await Assert.IsAssignableFrom<IResult>(result).ExecuteAsync(httpContext);

        Assert.Equal(StatusCodes.Status403Forbidden, httpContext.Response.StatusCode);
        Assert.Null(httpContext.Features.Get<AgentRoutingFeature>());
        Assert.Equal(0, selector.CallCount);
    }

    [Fact]
    public async Task InvokeAsync_ConversationHeader_PreservesEngineAffinity()
    {
        var selector = new StubSelector(new IntentAgentDecision("finance", 0.9, null));
        var routeTable = new StubRouteTable();
        AgentSelectionFilter filter = CreateFilter(selector, routeTable: routeTable);
        DefaultHttpContext httpContext = CreateHttpContext("{\"message\":\"find invoice\"}");
        httpContext.Request.Headers["X-Conversation-Id"] = "conversation-from-header";
        var invocation = new DefaultEndpointFilterInvocationContext(httpContext);

        await filter.InvokeAsync(
            invocation,
            _ => ValueTask.FromResult<object?>(Results.Ok()));

        Assert.Equal("conversation-from-header", routeTable.ConversationId);
    }

    [Fact]
    public async Task InvokeAsync_JsonWithInvalidShape_ReturnsBadRequest()
    {
        var selector = new StubSelector(new IntentAgentDecision("finance", 0.9, null));
        AgentSelectionFilter filter = CreateFilter(selector);
        DefaultHttpContext httpContext = CreateHttpContext("[]");
        var invocation = new DefaultEndpointFilterInvocationContext(httpContext);

        object? result = await filter.InvokeAsync(
            invocation,
            _ => ValueTask.FromResult<object?>(Results.Ok()));

        IStatusCodeHttpResult statusResult = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, statusResult.StatusCode);
        Assert.Equal(0, selector.CallCount);
        Assert.Equal(0, httpContext.Request.Body.Position);
    }

    private static AgentSelectionFilter CreateFilter(
        StubSelector selector,
        IReadOnlyList<RoutableAgent>? agents = null,
        TestGatewayAuthorizationService? authorization = null,
        StubRouteTable? routeTable = null)
    {
        var user = new AgentUserContext
        {
            UserId = "user-1",
            TenantId = "tenant-1",
            IsAuthenticated = true
        };
        IReadOnlyList<RoutableAgent> configuredAgents = agents ??
        [
            new RoutableAgent(
                new OpenAgent.Contracts.Configuration.AgentSummary
                {
                    AgentId = "finance",
                    Name = "Finance"
                },
                AgentDestinationKind.Engine,
                "http://engine"),
            new RoutableAgent(
                new OpenAgent.Contracts.Configuration.AgentSummary
                {
                    AgentId = "support",
                    Name = "Support"
                },
                AgentDestinationKind.Engine,
                "http://engine"),
            new RoutableAgent(
                new OpenAgent.Contracts.Configuration.AgentSummary
                {
                    AgentId = "default",
                    Name = "Default"
                },
                AgentDestinationKind.Engine,
                "http://engine")
        ];
        return new AgentSelectionFilter(
            routeTable ?? new StubRouteTable(),
            new StubCatalog(configuredAgents),
            new StubExternalAgentRegistry(configuredAgents),
            new AllowAllVisibilityService(),
            authorization ?? new TestGatewayAuthorizationService(),
            selector,
            user,
            Microsoft.Extensions.Options.Options.Create(new IntentRecognitionOptions
            {
                Enabled = true,
                AgentId = "intent-router",
                FallbackAgentId = "default",
                MinimumConfidence = 0.5,
                TimeoutMs = 5_000
            }),
            NullLogger<AgentSelectionFilter>.Instance);
    }

    private static DefaultHttpContext CreateHttpContext(string body)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider()
        };
        context.Request.Method = HttpMethods.Post;
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Request.ContentLength = context.Request.Body.Length;
        return context;
    }

    private sealed class StubSelector(IntentAgentDecision? decision) : IIntentAgentSelector
    {
        public int CallCount { get; private set; }

        public Task<IntentAgentDecision?> SelectAsync(
            IntentAgentSelectionRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(decision);
        }
    }

    private sealed class StubRouteTable : IRouteTable
    {
        public string? ConversationId { get; private set; }

        public string? GetTargetEndpoint(string intent) => "http://engine";

        public string? GetTargetEndpoint(
            string intent,
            string? tenantId,
            string? conversationId)
        {
            ConversationId = conversationId;
            return "http://engine";
        }
    }

    private sealed class StubCatalog(IReadOnlyList<RoutableAgent> agents) : IAgentCatalog
    {
        public Task<IReadOnlyList<RoutableAgent>> ListAsync(
            AgentCatalogRequest request,
            CancellationToken cancellationToken) => Task.FromResult(agents);
    }

    private sealed class StubExternalAgentRegistry : IExternalAgentRegistry
    {
        private readonly IReadOnlyDictionary<string, ExternalAgentOptions> _agents;

        public StubExternalAgentRegistry(IReadOnlyList<RoutableAgent> agents)
        {
            _agents = agents
                .Where(agent => agent.DestinationKind == AgentDestinationKind.External)
                .ToDictionary(
                    agent => agent.Summary.AgentId,
                    agent => new ExternalAgentOptions
                    {
                        AgentId = agent.Summary.AgentId,
                        Name = agent.Summary.Name,
                        BaseUrl = agent.TargetEndpoint
                    },
                    StringComparer.OrdinalIgnoreCase);
        }

        public IReadOnlyList<OpenAgent.Contracts.Configuration.AgentSummary> ListAgents() => [];

        public bool TryGet(string agentId, out ExternalAgentOptions? agent) =>
            _agents.TryGetValue(agentId, out agent);
    }

    private sealed class AllowAllVisibilityService : IAgentVisibilityService
    {
        public Task<bool> IsAgentVisibleToUserAsync(
            string agentId,
            IAgentUserContext userContext,
            CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<List<string>> GetPublishedAgentIdsAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(new List<string>());

        public Task<string?> GetAgentConfigAsync(
            string agentId,
            CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }
}
