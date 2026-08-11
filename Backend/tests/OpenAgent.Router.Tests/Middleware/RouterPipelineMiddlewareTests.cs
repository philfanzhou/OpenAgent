using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OpenAgent.Contracts.Security;
using OpenAgent.Router.Middleware;
using Xunit;

namespace OpenAgent.Router.Tests.Middleware;

public class RouterPipelineMiddlewareTests
{
    [Fact]
    public async Task TenantIsolation_MismatchedTenant_ReturnsForbidden()
    {
        bool nextCalled = false;
        var middleware = new TenantIsolationMiddleware(
            _ => { nextCalled = true; return Task.CompletedTask; },
            NullLogger<TenantIsolationMiddleware>.Instance);
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Tenant-Id"] = "tenant-2";

        await middleware.InvokeAsync(context, AuthenticatedUser);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task TenantIsolation_AuthenticatedRequest_StoresTrustedTenant()
    {
        bool nextCalled = false;
        var middleware = new TenantIsolationMiddleware(
            _ => { nextCalled = true; return Task.CompletedTask; },
            NullLogger<TenantIsolationMiddleware>.Instance);
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Tenant-Id"] = "tenant-1";

        await middleware.InvokeAsync(context, AuthenticatedUser);

        Assert.True(nextCalled);
        Assert.Equal("tenant-1", context.Items[TenantIsolationMiddleware.TenantItemKey]);
    }

    [Fact]
    public async Task TenantIsolation_AuthenticatedIdentityWithoutTenant_ReturnsForbidden()
    {
        bool nextCalled = false;
        var middleware = new TenantIsolationMiddleware(
            _ => { nextCalled = true; return Task.CompletedTask; },
            NullLogger<TenantIsolationMiddleware>.Instance);
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Tenant-Id"] = "spoofed-tenant";
        var user = new AgentUserContext
        {
            UserId = "user-1",
            TenantId = null,
            IsAuthenticated = true
        };

        await middleware.InvokeAsync(context, user);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.False(nextCalled);
        Assert.False(context.Items.ContainsKey(TenantIsolationMiddleware.TenantItemKey));
    }

    [Fact]
    public async Task RateLimiting_DeniedRequest_ReturnsTooManyRequests()
    {
        bool nextCalled = false;
        var middleware = new RateLimitingMiddleware(
            _ => { nextCalled = true; return Task.CompletedTask; },
            NullLogger<RateLimitingMiddleware>.Instance);
        var context = new DefaultHttpContext();
        context.Items[TenantIsolationMiddleware.TenantItemKey] = "tenant-1";
        var limiter = new StubRateLimiter(false);

        await middleware.InvokeAsync(context, AuthenticatedUser, limiter);

        Assert.Equal(StatusCodes.Status429TooManyRequests, context.Response.StatusCode);
        Assert.Equal("tenant-1:user-1", limiter.ClientId);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task RateLimiting_AllowedRequest_ContinuesPipeline()
    {
        bool nextCalled = false;
        var middleware = new RateLimitingMiddleware(
            _ => { nextCalled = true; return Task.CompletedTask; },
            NullLogger<RateLimitingMiddleware>.Instance);
        var limiter = new StubRateLimiter(true);

        await middleware.InvokeAsync(new DefaultHttpContext(), AuthenticatedUser, limiter);

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task RateLimiting_AnonymousRequest_BypassesLimiter()
    {
        bool nextCalled = false;
        var middleware = new RateLimitingMiddleware(
            _ => { nextCalled = true; return Task.CompletedTask; },
            NullLogger<RateLimitingMiddleware>.Instance);
        var limiter = new StubRateLimiter(false);

        await middleware.InvokeAsync(new DefaultHttpContext(), AnonymousUser, limiter);

        Assert.True(nextCalled);
        Assert.Null(limiter.ClientId);
    }

    [Fact]
    public async Task Idempotency_CachedResponse_ShortCircuitsPipeline()
    {
        bool nextCalled = false;
        var middleware = new IdempotencyMiddleware(
            _ => { nextCalled = true; return Task.CompletedTask; },
            NullLogger<IdempotencyMiddleware>.Instance);
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.Request.Headers["Idempotency-Key"] = "request-1";
        Mock<IDistributedCache> cache = new();
        cache.Setup(value => value.GetAsync("idempotency:request-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(System.Text.Encoding.UTF8.GetBytes("{\"cached\":true}"));

        await middleware.InvokeAsync(context, AuthenticatedUser, cache.Object);
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);

        Assert.False(nextCalled);
        Assert.Equal("application/json", context.Response.ContentType);
        Assert.Equal("{\"cached\":true}", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task Idempotency_CacheFailure_ContinuesPipeline()
    {
        bool nextCalled = false;
        var middleware = new IdempotencyMiddleware(
            _ => { nextCalled = true; return Task.CompletedTask; },
            NullLogger<IdempotencyMiddleware>.Instance);
        var context = new DefaultHttpContext();
        context.Request.Headers["Idempotency-Key"] = "request-1";
        Mock<IDistributedCache> cache = new();
        cache.Setup(value => value.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("cache unavailable"));

        await middleware.InvokeAsync(context, AuthenticatedUser, cache.Object);

        Assert.True(nextCalled);
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

    private sealed class StubRateLimiter(bool allowed) : IRateLimiter
    {
        public string? ClientId { get; private set; }

        public Task<bool> IsAllowedAsync(
            string clientId,
            CancellationToken cancellationToken = default)
        {
            ClientId = clientId;
            return Task.FromResult(allowed);
        }
    }
}
