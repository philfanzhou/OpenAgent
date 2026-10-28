using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Router.Options;
using Xunit;

namespace OpenAgent.Router.Tests.Middleware;

public class RedisRateLimiterTests
{
    [Theory]
    [InlineData("FailOpen", true, "fail_open")]
    [InlineData("FailClosed", false, "fail_closed")]
    [InlineData("Local", true, "local")]
    public async Task AcquireAsync_RedisUnavailable_UsesConfiguredFailureMode(
        string failureMode,
        bool expectedAllowed,
        string expectedSource)
    {
        RedisRateLimiter limiter = new(
            new RateLimitSettings(
                1,
                2,
                Enum.Parse<RateLimitFailureMode>(failureMode)),
            NullLogger<RedisRateLimiter>.Instance,
            redis: null,
            TimeProvider.System);

        RateLimitDecision decision = await limiter.AcquireAsync("client-1");

        Assert.Equal(expectedAllowed, decision.IsAllowed);
        Assert.Equal(expectedSource, decision.Source);
        Assert.True(decision.IsDegraded);
    }

    [Fact]
    public async Task AcquireAsync_LocalFallbackConcurrentRequests_EnforcesBurst()
    {
        RedisRateLimiter limiter = new(
            new RateLimitSettings(0.001, 5, RateLimitFailureMode.Local),
            NullLogger<RedisRateLimiter>.Instance,
            redis: null,
            TimeProvider.System);

        RateLimitDecision[] decisions = await Task.WhenAll(
            Enumerable.Range(0, 50).Select(_ => limiter.AcquireAsync("client-1")));

        Assert.Equal(5, decisions.Count(decision => decision.IsAllowed));
        Assert.All(decisions, decision => Assert.Equal("local", decision.Source));
    }
}
