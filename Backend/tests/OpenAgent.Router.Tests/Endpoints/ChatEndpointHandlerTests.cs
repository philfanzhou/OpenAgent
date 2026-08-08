using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Contracts.Security;
using OpenAgent.Router.Endpoints;
using OpenAgent.Router.Middleware;
using OpenAgent.Router.Models;
using Xunit;
using Yarp.ReverseProxy.Forwarder;

namespace OpenAgent.Router.Tests.Endpoints;

public class ChatEndpointHandlerTests
{
    private static readonly HttpMessageInvoker HttpClient = new(new HttpClientHandler());
    private static readonly ForwarderRequestConfig RequestConfig = new();

    [Fact]
    public async Task HandleAsync_AnonymousRequest_ReturnsUnauthorized()
    {
        var context = CreateContext();
        var forwarder = new CapturingForwarder();

        IResult result = await HandleAsync(
            context,
            forwarder,
            new StubExternalForwarder(),
            AnonymousUser);
        await result.ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.Null(forwarder.ProxyRequest);
    }

    [Fact]
    public async Task HandleAsync_NoRoutingFeature_ReturnsInternalServerError()
    {
        var context = CreateContext();

        IResult result = await HandleAsync(
            context,
            new CapturingForwarder(),
            new StubExternalForwarder(),
            AuthenticatedUser);
        await result.ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
    }

    [Fact]
    public async Task HandleAsync_EngineRoute_ForwardsResolvedAgentAndTrustedContext()
    {
        var context = CreateContext();
        context.Items[TenantIsolationMiddleware.TenantItemKey] = "tenant-1";
        context.Features.Set(new AgentRoutingFeature(
            new ParsedChatRequest("hello", "conversation-1", null),
            "support",
            "http://engine:5100",
            AgentDestinationKind.Engine,
            SelectedByIntentAgent: true));
        var forwarder = new CapturingForwarder();

        await HandleAsync(
            context,
            forwarder,
            new StubExternalForwarder(),
            AuthenticatedUser);

        Assert.Equal(
            "http://engine:5100/api/v1/agent/chat/stream",
            forwarder.ProxyRequest?.RequestUri?.ToString());
        Assert.Equal("support", GetSingleHeader(forwarder.ProxyRequest, "X-OpenAgent-Resolved-Agent-Id"));
        Assert.Equal("user-1", GetSingleHeader(forwarder.ProxyRequest, "X-User-Id"));
        Assert.Equal("tenant-1", GetSingleHeader(forwarder.ProxyRequest, "X-Tenant-Id"));
        Assert.Equal("conversation-1", GetSingleHeader(forwarder.ProxyRequest, "X-Conversation-Id"));
    }

    [Fact]
    public async Task HandleAsync_ExternalRoute_UsesExternalForwarder()
    {
        var context = CreateContext();
        context.Features.Set(new AgentRoutingFeature(
            new ParsedChatRequest("hello", null, "external-support"),
            "external-support",
            "https://partner.example",
            AgentDestinationKind.External,
            SelectedByIntentAgent: false));
        var engineForwarder = new CapturingForwarder();
        var externalForwarder = new StubExternalForwarder
        {
            Result = new ExternalForwardingResult(
                ForwarderError.None,
                "https://partner.example",
                "https://partner.example/chat/stream")
        };

        await HandleAsync(
            context,
            engineForwarder,
            externalForwarder,
            AuthenticatedUser);

        Assert.Equal(1, externalForwarder.CallCount);
        Assert.Equal("external-support", externalForwarder.AgentId);
        Assert.Equal("stream", externalForwarder.Action);
        Assert.Null(engineForwarder.ProxyRequest);
    }

    private static Task<IResult> HandleAsync(
        HttpContext context,
        IHttpForwarder forwarder,
        IExternalAgentForwarder externalForwarder,
        IAgentUserContext userContext) => ChatEndpointHandler.HandleAsync(
            "stream",
            context,
            forwarder,
            externalForwarder,
            userContext,
            NullLogger.Instance,
            HttpClient,
            RequestConfig,
            context.RequestAborted);

    private static DefaultHttpContext CreateContext()
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider()
        };
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/api/v1/agent/chat/stream";
        return context;
    }

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

    private static string GetSingleHeader(HttpRequestMessage? request, string headerName)
    {
        Assert.NotNull(request);
        return Assert.Single(request.Headers.GetValues(headerName));
    }

    private sealed class CapturingForwarder : IHttpForwarder
    {
        public HttpRequestMessage? ProxyRequest { get; private set; }

        public ValueTask<ForwarderError> SendAsync(
            HttpContext context,
            string destinationPrefix,
            HttpMessageInvoker httpClient,
            ForwarderRequestConfig requestConfig,
            HttpTransformer transformer) => SendAsync(
                context,
                destinationPrefix,
                httpClient,
                requestConfig,
                transformer,
                context.RequestAborted);

        public async ValueTask<ForwarderError> SendAsync(
            HttpContext context,
            string destinationPrefix,
            HttpMessageInvoker httpClient,
            ForwarderRequestConfig requestConfig,
            HttpTransformer transformer,
            CancellationToken cancellationToken)
        {
            ProxyRequest = new HttpRequestMessage(
                new HttpMethod(context.Request.Method),
                destinationPrefix);
            await transformer.TransformRequestAsync(
                context,
                ProxyRequest,
                destinationPrefix,
                cancellationToken);
            return ForwarderError.None;
        }
    }

    private sealed class StubExternalForwarder : IExternalAgentForwarder
    {
        public ExternalForwardingResult? Result { get; init; }
        public int CallCount { get; private set; }
        public string? AgentId { get; private set; }
        public string? Action { get; private set; }

        public Task<ExternalForwardingResult?> ForwardAsync(
            HttpContext context,
            string agentId,
            string? action,
            IAgentUserContext userContext,
            string? tenantId,
            string? conversationId,
            string traceId,
            CancellationToken cancellationToken)
        {
            CallCount++;
            AgentId = agentId;
            Action = action;
            return Task.FromResult(Result);
        }
    }
}
