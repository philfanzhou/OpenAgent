using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Contracts.Security;
using OpenAgent.Router.Middleware;
using StackExchange.Redis;
using Xunit;

namespace OpenAgent.Router.Tests.Middleware;

public class IdempotencyMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_CompletedRequest_ReplaysResponseMetadataAndBody()
    {
        int callCount = 0;
        var middleware = CreateMiddleware(async context =>
        {
            callCount++;
            context.Response.StatusCode = StatusCodes.Status201Created;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync("{\"created\":true}");
        });
        var store = new IdempotencyStore();
        AgentUserContext user = CacheMiddlewareTestHelper.CreateUser();
        DefaultHttpContext first = CreateIdempotentContext();
        DefaultHttpContext second = CreateIdempotentContext();

        await middleware.InvokeAsync(first, user, store);
        await middleware.InvokeAsync(second, user, store);

        Assert.Equal(1, callCount);
        Assert.Equal(StatusCodes.Status201Created, second.Response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", second.Response.ContentType);
        Assert.Equal("{\"created\":true}", await CacheMiddlewareTestHelper.ReadResponseAsync(second));
    }

    [Fact]
    public async Task InvokeAsync_EquivalentJsonWithDifferentPropertyOrder_ReplaysResponse()
    {
        int callCount = 0;
        var middleware = CreateMiddleware(context =>
        {
            callCount++;
            context.Response.ContentType = "application/json";
            return context.Response.WriteAsync("{\"ok\":true}");
        });
        var store = new IdempotencyStore();
        AgentUserContext user = CacheMiddlewareTestHelper.CreateUser();
        DefaultHttpContext first = CreateIdempotentContext(
            "{\"message\":\"hello\",\"context\":{\"agentId\":\"a\"}}");
        DefaultHttpContext second = CreateIdempotentContext(
            "{ \"context\": { \"agentId\": \"a\" }, \"message\": \"hello\" }");

        await middleware.InvokeAsync(first, user, store);
        await middleware.InvokeAsync(second, user, store);

        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task InvokeAsync_KeyReusedWithDifferentRequest_ReturnsConflict()
    {
        int callCount = 0;
        var middleware = CreateMiddleware(context =>
        {
            callCount++;
            context.Response.ContentType = "application/json";
            return context.Response.WriteAsync("{}");
        });
        var store = new IdempotencyStore();
        AgentUserContext user = CacheMiddlewareTestHelper.CreateUser();
        DefaultHttpContext first = CreateIdempotentContext("{\"message\":\"first\"}");
        DefaultHttpContext second = CreateIdempotentContext("{\"message\":\"second\"}");

        await middleware.InvokeAsync(first, user, store);
        await middleware.InvokeAsync(second, user, store);

        Assert.Equal(1, callCount);
        Assert.Equal(StatusCodes.Status409Conflict, second.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_ConcurrentDuplicate_ReturnsConflict()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var middleware = CreateMiddleware(async context =>
        {
            entered.TrySetResult();
            await release.Task;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{}");
        });
        var store = new IdempotencyStore();
        AgentUserContext user = CacheMiddlewareTestHelper.CreateUser();
        DefaultHttpContext first = CreateIdempotentContext();
        DefaultHttpContext concurrent = CreateIdempotentContext();

        Task firstRequest = middleware.InvokeAsync(first, user, store);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await middleware.InvokeAsync(concurrent, user, store);
        release.TrySetResult();
        await firstRequest;

        Assert.Equal(StatusCodes.Status409Conflict, concurrent.Response.StatusCode);
        Assert.Equal("1", concurrent.Response.Headers.RetryAfter);
    }

    [Fact]
    public async Task InvokeAsync_SameClientKeyAcrossSecurityAndRouteScopes_ExecutesIndependently()
    {
        int callCount = 0;
        var middleware = CreateMiddleware(context =>
        {
            callCount++;
            context.Response.ContentType = "application/json";
            return context.Response.WriteAsync("{}");
        });
        var store = new IdempotencyStore();

        await middleware.InvokeAsync(
            CreateIdempotentContext(),
            CacheMiddlewareTestHelper.CreateUser(),
            store);

        DefaultHttpContext otherTenant = CreateIdempotentContext();
        await middleware.InvokeAsync(
            otherTenant,
            CacheMiddlewareTestHelper.CreateUser(tenantId: "tenant-2"),
            store);

        await middleware.InvokeAsync(
            CreateIdempotentContext(),
            CacheMiddlewareTestHelper.CreateUser(userId: "user-2"),
            store);
        await middleware.InvokeAsync(
            CreateIdempotentContext(path: "/api/v1/agent/chat/complete"),
            CacheMiddlewareTestHelper.CreateUser(),
            store);

        Assert.Equal(4, callCount);
    }

    [Fact]
    public async Task InvokeAsync_CanceledExecution_ReleasesPlaceholderForRetry()
    {
        int callCount = 0;
        var middleware = CreateMiddleware(context =>
        {
            callCount++;
            if (callCount == 1)
            {
                throw new OperationCanceledException("downstream canceled");
            }

            context.Response.ContentType = "application/json";
            return context.Response.WriteAsync("{}");
        });
        var store = new IdempotencyStore();
        AgentUserContext user = CacheMiddlewareTestHelper.CreateUser();

        await Assert.ThrowsAsync<OperationCanceledException>(() => middleware.InvokeAsync(
            CreateIdempotentContext(),
            user,
            store));
        await middleware.InvokeAsync(CreateIdempotentContext(), user, store);

        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task InvokeAsync_ErrorResponse_ReleasesPlaceholderForRetry()
    {
        int callCount = 0;
        var middleware = CreateMiddleware(context =>
        {
            callCount++;
            context.Response.StatusCode = callCount == 1
                ? StatusCodes.Status503ServiceUnavailable
                : StatusCodes.Status200OK;
            context.Response.ContentType = "application/json";
            return context.Response.WriteAsync("{}");
        });
        var store = new IdempotencyStore();
        AgentUserContext user = CacheMiddlewareTestHelper.CreateUser();

        await middleware.InvokeAsync(CreateIdempotentContext(), user, store);
        await middleware.InvokeAsync(CreateIdempotentContext(), user, store);

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
            CreateIdempotentContext(),
            CacheMiddlewareTestHelper.CreateUser(),
            new ThrowingIdempotencyStore());

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task InvokeAsync_StreamRoute_BypassesIdempotencyStore()
    {
        bool nextCalled = false;
        var middleware = CreateMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        DefaultHttpContext context = CreateIdempotentContext(path: "/api/v1/agent/chat/stream");

        await middleware.InvokeAsync(
            context,
            CacheMiddlewareTestHelper.CreateUser(),
            new ThrowingIdempotencyStore());

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
            await context.Response.WriteAsync("data: message\n\n");
        });
        var store = new IdempotencyStore();
        AgentUserContext user = CacheMiddlewareTestHelper.CreateUser();

        await middleware.InvokeAsync(CreateIdempotentContext(), user, store);
        await middleware.InvokeAsync(CreateIdempotentContext(), user, store);

        Assert.Equal(2, callCount);
    }

    private static IdempotencyMiddleware CreateMiddleware(RequestDelegate next) => new(
        next,
        NullLogger<IdempotencyMiddleware>.Instance,
        CacheMiddlewareTestHelper.CreateSettings());

    private static DefaultHttpContext CreateIdempotentContext(
        string body = "{\"message\":\"hello\"}",
        string path = "/api/v1/agent/chat")
    {
        DefaultHttpContext context = CacheMiddlewareTestHelper.CreateContext(body, path);
        context.Request.Headers["Idempotency-Key"] = "request-1";
        return context;
    }

    private sealed class ThrowingIdempotencyStore : IIdempotencyStore
    {
        public Task<IdempotencyAcquireResult> AcquireAsync(
            string key,
            string requestDigest,
            string ownerToken,
            TimeSpan timeToLive,
            CancellationToken cancellationToken = default)
        {
            throw new RedisConnectionException(
                ConnectionFailureType.UnableToConnect,
                "Redis is unavailable.");
        }

        public Task<bool> CompleteAsync(
            string key,
            string requestDigest,
            string ownerToken,
            CachedResponse response,
            TimeSpan timeToLive,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task ReleaseAsync(
            string key,
            string requestDigest,
            string ownerToken,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
