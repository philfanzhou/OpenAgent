using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Moq;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Models;
using OpenAgent.Engine.Abstractions;
using OpenAgent.Engine.Redis;
using Xunit;

namespace OpenAgent.Engine.Tests.HealthChecks;

public class ConfigHealthCheckTests
{
    [Fact]
    public async Task Returns_healthy_when_no_published_agents()
    {
        var redis = new FakeRedisConnectionProvider();
        var check = new ConfigHealthCheck(redis, Repository());

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Contains("Agents: 0", result.Description);
    }

    [Fact]
    public async Task Returns_healthy_when_published_agents_are_not_cached()
    {
        var redis = new FakeRedisConnectionProvider();
        await redis.SetAddAsync("agent:published:index", "agent-ok");

        var check = new ConfigHealthCheck(redis, Repository(1));

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Contains("Redis cache", result.Description);
    }

    [Fact]
    public async Task Returns_healthy_when_snapshot_is_empty()
    {
        var redis = new FakeRedisConnectionProvider();
        await redis.SetAddAsync("agent:published:index", "agent-missing");

        var check = new ConfigHealthCheck(redis, Repository(1));

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task Returns_healthy_when_snapshot_is_partially_populated()
    {
        var redis = new FakeRedisConnectionProvider();
        await redis.SetAddAsync("agent:published:index", "agent-ok");
        await redis.SetAddAsync("agent:published:index", "agent-missing");

        var check = new ConfigHealthCheck(redis, Repository(2));

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task Returns_degraded_when_redis_not_available()
    {
        var redis = new FakeRedisConnectionProvider { IsAvailable = false };
        var check = new ConfigHealthCheck(redis, Repository(1));

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("Redis cache is unavailable", result.Description);
    }

    private static IAgentConfigRepository Repository(int count = 0)
    {
        var repository = new Mock<IAgentConfigRepository>();
        repository.Setup(item => item.ListAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Range(0, count).Select(index => new AgentConfigEntity
            {
                TenantId = "tenant-a",
                AgentId = $"agent-{index}"
            }).ToArray());
        return repository.Object;
    }
}
