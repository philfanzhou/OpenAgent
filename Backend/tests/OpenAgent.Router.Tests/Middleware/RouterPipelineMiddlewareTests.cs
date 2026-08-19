using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Contracts.Security;
using OpenAgent.Router.Middleware;
using Xunit;

namespace OpenAgent.Router.Tests.Middleware;

public class RouterPipelineMiddlewareTests
{
    [Fact]
    public async Task RateLimiting_DeniedRequest_ReturnsTooManyRequests()
    {
        bool nextCalled = false;
        var middleware = new RateLimitingMiddleware(
            _ => { nextCalled = true; return Task.CompletedTask; },
            NullLogger<RateLimitingMiddleware>.Instance);
        var context = new DefaultHttpContext();
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
