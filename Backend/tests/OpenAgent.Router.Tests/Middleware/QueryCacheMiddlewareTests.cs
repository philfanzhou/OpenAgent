using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Contracts.Security;
using OpenAgent.Router.Middleware;
using StackExchange.Redis;
using Xunit;

namespace OpenAgent.Router.Tests.Middleware;

public class QueryCacheMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_PublicSuccessfulResponse_ReplaysCachedResponse()
    {
        int callCount = 0;
        var middleware = CreateMiddleware(async context =>
        {
            callCount++;
            context.Response.StatusCode = StatusCodes.Status203NonAuthoritative;
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.Headers.CacheControl = "public, max-age=60";
            await context.Response.WriteAsync("{\"cached\":true}");
        });
        var cache = new RouterQueryCache();
        AgentUserContext user = CacheMiddlewareTestHelper.CreateUser();
        DefaultHttpContext first = CacheMiddlewareTestHelper.CreateContext(
            "{\"message\":\"hello\",\"context\":{\"agentId\":\"a\"}}");
        DefaultHttpContext second = CacheMiddlewareTestHelper.CreateContext(
            "{ \"context\": { \"agentId\": \"a\" }, \"message\": \"hello\" }");

        await middleware.InvokeAsync(first, user, cache);
        await middleware.InvokeAsync(second, user, cache);

        Assert.Equal(1, callCount);
        Assert.Equal(StatusCodes.Status203NonAuthoritative, second.Response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", second.Response.ContentType);
        Assert.Equal("{\"cached\":true}", await CacheMiddlewareTestHelper.ReadResponseAsync(second));
    }

    [Fact]
    public async Task InvokeAsync_CacheScopesDiffer_ExecutesIndependently()
    {
        int callCount = 0;
        var middleware = CreateMiddleware(context => WritePublicResponseAsync(context, () => callCount++));
        var cache = new RouterQueryCache();

        await middleware.InvokeAsync(
            CacheMiddlewareTestHelper.CreateContext(),
            CacheMiddlewareTestHelper.CreateUser(),
            cache);

        DefaultHttpContext otherTenant = CacheMiddlewareTestHelper.CreateContext();
        otherTenant.Items[TenantIsolationMiddleware.TenantItemKey] = "tenant-2";
        await middleware.InvokeAsync(
            otherTenant,
            CacheMiddlewareTestHelper.CreateUser(tenantId: "tenant-2"),
            cache);
        await middleware.InvokeAsync(
            CacheMiddlewareTestHelper.CreateContext(),
            CacheMiddlewareTestHelper.CreateUser(userId: "user-2"),
            cache);
        await middleware.InvokeAsync(
            CacheMiddlewareTestHelper.CreateContext(path: "/api/v1/agent/chat/complete"),
            CacheMiddlewareTestHelper.CreateUser(),
            cache);
        await middleware.InvokeAsync(
            CacheMiddlewareTestHelper.CreateContext("{\"message\":\"different\"}"),
            CacheMiddlewareTestHelper.CreateUser(),
            cache);

        Assert.Equal(5, callCount);
    }

    [Theory]
    [InlineData("{\"message\":\"hello\",\"fileIds\":[\"file-1\"]}", false)]
    [InlineData("{\"message\":\"hello\",\"context\":{\"conversationId\":\"c-1\"}}", false)]
    [InlineData("{\"message\":\"hello\",\"context\":{\"userPreference\":\"private\"}}", false)]
    [InlineData("{\"message\":\"hello\",\"context\":{\"agentId\":\"public-agent\"}}", true)]
    public async Task InvokeAsync_RequestSensitivity_ControlsCaching(
        string body,
        bool expectedToCache)
    {
        int callCount = 0;
        var middleware = CreateMiddleware(context => WritePublicResponseAsync(context, () => callCount++));
        var cache = new RouterQueryCache();
        AgentUserContext user = CacheMiddlewareTestHelper.CreateUser();

        await middleware.InvokeAsync(CacheMiddlewareTestHelper.CreateContext(body), user, cache);
        await middleware.InvokeAsync(CacheMiddlewareTestHelper.CreateContext(body), user, cache);

        Assert.Equal(expectedToCache ? 1 : 2, callCount);
    }

    [Theory]
    [InlineData(StatusCodes.Status500InternalServerError, "public")]
    [InlineData(StatusCodes.Status200OK, "private")]
    [InlineData(StatusCodes.Status200OK, "no-store")]
    public async Task InvokeAsync_UncacheableResponse_DoesNotPersist(
        int statusCode,
        string cacheControl)
    {
        int callCount = 0;
        var middleware = CreateMiddleware(async context =>
        {
            callCount++;
            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "application/json";
            context.Response.Headers.CacheControl = cacheControl;
            await context.Response.WriteAsync("{}");
        });
        var cache = new RouterQueryCache();
        AgentUserContext user = CacheMiddlewareTestHelper.CreateUser();

        await middleware.InvokeAsync(CacheMiddlewareTestHelper.CreateContext(), user, cache);
        await middleware.InvokeAsync(CacheMiddlewareTestHelper.CreateContext(), user, cache);

        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task InvokeAsync_RedisFailure_FailsOpen()
    {
        bool nextCalled = false;
        var middleware = CreateMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(
            CacheMiddlewareTestHelper.CreateContext(),
            CacheMiddlewareTestHelper.CreateUser(),
            new ThrowingQueryCache());

        Assert.True(nextCalled);
    }

    [Theory]
    [InlineData("/api/v1/agent/chat/stream", null)]
    [InlineData("/api/v1/agent/chat/sse", null)]
    [InlineData("/api/v1/agent/chat", "text/event-stream")]
    public async Task InvokeAsync_StreamingRequest_BypassesCache(string path, string? accept)
    {
        bool nextCalled = false;
        var middleware = CreateMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        DefaultHttpContext context = CacheMiddlewareTestHelper.CreateContext(path: path);
        if (accept != null)
        {
            context.Request.Headers.Accept = accept;
        }

        await middleware.InvokeAsync(
            context,
            CacheMiddlewareTestHelper.CreateUser(),
            new ThrowingQueryCache());

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task InvokeAsync_StreamingResponse_DoesNotPersist()
    {
        int callCount = 0;
        var middleware = CreateMiddleware(async context =>
        {
            callCount++;
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "public";
            await context.Response.WriteAsync("data: message\n\n");
        });
        var cache = new RouterQueryCache();
        AgentUserContext user = CacheMiddlewareTestHelper.CreateUser();

        await middleware.InvokeAsync(CacheMiddlewareTestHelper.CreateContext(), user, cache);
        await middleware.InvokeAsync(CacheMiddlewareTestHelper.CreateContext(), user, cache);

        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task GetAsync_RepeatedCall_ReusesRequestBodySnapshotAndRewindsBody()
    {
        DefaultHttpContext context = CacheMiddlewareTestHelper.CreateContext();

        Task<RequestBodySnapshot> first = RequestBodySnapshot.GetAsync(context, 1024);
        Task<RequestBodySnapshot> second = RequestBodySnapshot.GetAsync(context, 1024);
        RequestBodySnapshot snapshot = await first;

        Assert.Same(first, second);
        Assert.NotEmpty(snapshot.Digest);
        Assert.Equal(0, context.Request.Body.Position);
    }

    [Fact]
    public async Task InvokeAsync_OversizedBody_BypassesCacheAndPreservesBody()
    {
        int downstreamBodyLength = 0;
        var settings = CacheMiddlewareTestHelper.CreateSettings(new Dictionary<string, string?>
        {
            ["RouterSettings:Caching:MaxRequestBodyBytes"] = "1024"
        });
        var middleware = new QueryCacheMiddleware(
            async context =>
            {
                using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
                downstreamBodyLength = (await reader.ReadToEndAsync()).Length;
            },
            NullLogger<QueryCacheMiddleware>.Instance,
            settings);
        string body = "{\"message\":\"" + new string('x', 2048) + "\"}";
        DefaultHttpContext context = CacheMiddlewareTestHelper.CreateContext(body);

        await middleware.InvokeAsync(
            context,
            CacheMiddlewareTestHelper.CreateUser(),
            new ThrowingQueryCache());

        Assert.Equal(body.Length, downstreamBodyLength);
    }

    private static QueryCacheMiddleware CreateMiddleware(RequestDelegate next) => new(
        next,
        NullLogger<QueryCacheMiddleware>.Instance,
        CacheMiddlewareTestHelper.CreateSettings());

    private static async Task WritePublicResponseAsync(
        HttpContext context,
        Action onCall)
    {
        onCall();
        context.Response.ContentType = "application/json";
        context.Response.Headers.CacheControl = "public, max-age=60";
        await context.Response.WriteAsync("{}");
    }

    private sealed class ThrowingQueryCache : IQueryCache
    {
        public Task<string?> GetCachedResponseAsync(
            string query,
            CancellationToken cancellationToken = default)
        {
            throw CreateException();
        }

        public Task SetCachedResponseAsync(
            string query,
            string response,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CachedResponse?> GetAsync(
            string key,
            CancellationToken cancellationToken = default)
        {
            throw CreateException();
        }

        public Task SetAsync(
            string key,
            CachedResponse response,
            TimeSpan timeToLive,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        private static RedisConnectionException CreateException() => new(
            ConnectionFailureType.UnableToConnect,
            "Redis is unavailable.");
    }
}
