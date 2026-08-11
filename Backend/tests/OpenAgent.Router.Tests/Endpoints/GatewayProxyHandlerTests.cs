using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Contracts.Security;
using OpenAgent.Hosting.Authorization;
using OpenAgent.Router.Endpoints;
using Xunit;
using Yarp.ReverseProxy.Forwarder;

namespace OpenAgent.Router.Tests.Endpoints;

public class GatewayProxyHandlerTests
{
    private static readonly HttpMessageInvoker HttpClient = new(new HttpClientHandler());
    private static readonly ForwarderRequestConfig RequestConfig = new();

    [Fact]
    public async Task HandleAsync_AuthenticatedRequest_ForwardsOriginalPathWithTrustedIdentity()
    {
        var context = CreateContext(HttpMethods.Get, "/api/v1/admin/agents", "?take=10");
        context.Request.Headers["X-User-Id"] = "spoofed-user";
        context.Request.Headers["X-Tenant-Id"] = "spoofed-tenant";
        context.Request.Headers["X-Conversation-Id"] = "conversation-1";
        context.Request.Headers["X-Agent-Id"] = "spoofed-agent";
        var forwarder = new CapturingForwarder();
        var routeTable = new StubRouteTable("http://engine:5100/root");
        var user = new AgentUserContext
        {
            UserId = "trusted-user",
            TenantId = "trusted-tenant",
            IsAuthenticated = true
        };

        await GatewayProxyHandler.HandleAsync(
            context,
            forwarder,
            user,
            routeTable,
            NullLogger.Instance,
            HttpClient,
            RequestConfig,
            requireAuthentication: true);

        Assert.Equal("chat", routeTable.Intent);
        Assert.Equal("trusted-tenant", routeTable.TenantId);
        Assert.Equal("conversation-1", routeTable.ConversationId);
        Assert.Equal("http://engine:5100/root", forwarder.DestinationPrefix);
        Assert.Equal(
            "http://engine:5100/root/api/v1/admin/agents?take=10",
            forwarder.ProxyRequest?.RequestUri?.ToString());
        Assert.Equal("trusted-user", GetSingleHeader(forwarder.ProxyRequest, "X-User-Id"));
        Assert.Equal("trusted-tenant", GetSingleHeader(forwarder.ProxyRequest, "X-Tenant-Id"));
        Assert.Equal("conversation-1", GetSingleHeader(forwarder.ProxyRequest, "X-Conversation-Id"));
        AssertHeaderMissing(forwarder.ProxyRequest, "X-Agent-Id");
    }

    [Fact]
    public async Task HandleAsync_AnonymousAuthRequest_StripsSpoofableRoutingHeaders()
    {
        var context = CreateContext(HttpMethods.Post, "/api/v1/auth/token", "?mode=basic");
        context.Request.Headers["X-Agent-Id"] = "spoofed-agent";
        context.Request.Headers["X-User-Id"] = "spoofed-user";
        context.Request.Headers["X-Tenant-Id"] = "spoofed-tenant";
        context.Request.Headers["X-Conversation-Id"] = "spoofed-conversation";
        context.Request.Headers["X-Trace-Id"] = "spoofed-trace";
        var forwarder = new CapturingForwarder();
        var routeTable = new StubRouteTable("http://engine:5100");

        await GatewayProxyHandler.HandleAsync(
            context,
            forwarder,
            AnonymousUser,
            routeTable,
            NullLogger.Instance,
            HttpClient,
            RequestConfig,
            requireAuthentication: false);

        Assert.Equal(
            "http://engine:5100/api/v1/auth/token?mode=basic",
            forwarder.ProxyRequest?.RequestUri?.ToString());
        Assert.Null(routeTable.TenantId);
        Assert.Null(routeTable.ConversationId);
        AssertHeaderMissing(forwarder.ProxyRequest, "X-Agent-Id");
        AssertHeaderMissing(forwarder.ProxyRequest, "X-User-Id");
        AssertHeaderMissing(forwarder.ProxyRequest, "X-Tenant-Id");
        AssertHeaderMissing(forwarder.ProxyRequest, "X-Conversation-Id");
        Assert.NotEqual("spoofed-trace", GetSingleHeader(forwarder.ProxyRequest, "X-Trace-Id"));
    }

    [Fact]
    public async Task HandleAsync_AuthenticationRequired_DoesNotForwardAnonymousRequest()
    {
        var context = CreateContext(HttpMethods.Get, "/api/v1/agent/me");
        var forwarder = new CapturingForwarder();

        IResult result = await GatewayProxyHandler.HandleAsync(
            context,
            forwarder,
            AnonymousUser,
            new StubRouteTable("http://engine:5100"),
            NullLogger.Instance,
            HttpClient,
            RequestConfig,
            requireAuthentication: true);
        await result.ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.Null(forwarder.ProxyRequest);
    }

    [Fact]
    public async Task HandleAsync_NoEngineAvailable_ReturnsServiceUnavailable()
    {
        var context = CreateContext(HttpMethods.Get, "/api/v1/agent/me");
        var forwarder = new CapturingForwarder();

        IResult result = await GatewayProxyHandler.HandleAsync(
            context,
            forwarder,
            AuthenticatedUser,
            new StubRouteTable(null),
            NullLogger.Instance,
            HttpClient,
            RequestConfig,
            requireAuthentication: true);
        await result.ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.Null(forwarder.ProxyRequest);
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

    private static DefaultHttpContext CreateContext(
        string method,
        string path,
        string? query = null)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddLogging()
                .AddSingleton<IGatewayAuthorizationService>(new TestGatewayAuthorizationService())
                .BuildServiceProvider()
        };
        context.Request.Method = method;
        context.Request.Path = path;
        if (query is not null)
        {
            context.Request.QueryString = new QueryString(query);
        }
        return context;
    }

    private static string GetSingleHeader(HttpRequestMessage? request, string headerName)
    {
        Assert.NotNull(request);
        return Assert.Single(request.Headers.GetValues(headerName));
    }

    private static void AssertHeaderMissing(HttpRequestMessage? request, string headerName)
    {
        Assert.NotNull(request);
        Assert.False(request.Headers.Contains(headerName));
    }

    private sealed class StubRouteTable(string? endpoint) : IRouteTable
    {
        public string? Intent { get; private set; }
        public string? TenantId { get; private set; }
        public string? ConversationId { get; private set; }

        public string? GetTargetEndpoint(string intent) => endpoint;

        public string? GetTargetEndpoint(string intent, string? tenantId, string? conversationId)
        {
            Intent = intent;
            TenantId = tenantId;
            ConversationId = conversationId;
            return endpoint;
        }
    }

    private sealed class CapturingForwarder : IHttpForwarder
    {
        public string? DestinationPrefix { get; private set; }
        public HttpRequestMessage? ProxyRequest { get; private set; }

        public ValueTask<ForwarderError> SendAsync(
            HttpContext context,
            string destinationPrefix,
            HttpMessageInvoker httpClient,
            ForwarderRequestConfig requestConfig,
            HttpTransformer transformer) =>
            SendAsync(
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
            DestinationPrefix = destinationPrefix;
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
}
