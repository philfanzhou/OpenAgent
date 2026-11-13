using Microsoft.Extensions.Diagnostics.HealthChecks;
using Moq;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Models;
using OpenAgent.Engine.Redis;
using Xunit;

namespace OpenAgent.Engine.Tests.HealthChecks;

public class ConfigHealthCheckTests
{
    [Fact]
    public async Task Returns_degraded_when_empty()
    {
        HealthCheckResult result = await new ConfigHealthCheck(Repository())
            .CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Equal(0, result.Data["agentCount"]);
    }

    [Fact]
    public async Task Aggregates_all_tenants_and_counts_published_agents()
    {
        HealthCheckResult result = await new ConfigHealthCheck(Repository(
            Agent("tenant-a", "a", AgentPublishStatus.Published),
            Agent("tenant-b", "b", AgentPublishStatus.Draft)))
            .CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal(2, result.Data["agentCount"]);
        Assert.Equal(1, result.Data["publishedAgentCount"]);
    }

    [Fact]
    public async Task Returns_unhealthy_when_agent_is_invalid()
    {
        AgentConfigEntity invalid = Agent("tenant-a", "a", AgentPublishStatus.Draft);
        invalid.Config.TenantId = "tenant-b";
        HealthCheckResult result = await new ConfigHealthCheck(Repository(invalid))
            .CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal(1, result.Data["invalidAgentCount"]);
    }

    [Theory]
    [InlineData(-1, HealthStatus.Unhealthy)]
    [InlineData(0, HealthStatus.Healthy)]
    [InlineData(1, HealthStatus.Healthy)]
    public async Task CheckHealthAsync_TurnLimit_RespectsRuntimeDefaultSemantics(int maxTurns, HealthStatus expected)
    {
        AgentConfigEntity agent = Agent("tenant-a", "a", AgentPublishStatus.Published);
        agent.Config.MaxTurns = maxTurns;

        HealthCheckResult result = await new ConfigHealthCheck(Repository(agent)).CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(expected, result.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CheckHealthAsync_RepositoryFailure_DistinguishesFailureFromCancellation(bool cancelled)
    {
        using CancellationTokenSource cancellation = new();
        if (cancelled) cancellation.Cancel();
        var repository = new Mock<IAgentConfigRepository>();
        repository.Setup(item => item.ListAsync(null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(cancelled ? new OperationCanceledException(cancellation.Token) : new InvalidOperationException("read failed"));
        var check = new ConfigHealthCheck(repository.Object);

        if (cancelled)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check.CheckHealthAsync(new HealthCheckContext(), cancellation.Token));
        }
        else
        {
            HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext());
            Assert.Equal(HealthStatus.Unhealthy, result.Status);
        }
    }

    private static AgentConfigEntity Agent(string tenant, string id, AgentPublishStatus status) => new()
    {
        TenantId = tenant,
        AgentId = id,
        Status = status,
        Config = new AgentConfig { TenantId = tenant }
    };

    private static IAgentConfigRepository Repository(params AgentConfigEntity[] agents)
    {
        var repository = new Mock<IAgentConfigRepository>();
        repository.Setup(item => item.ListAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(agents);
        return repository.Object;
    }
}
