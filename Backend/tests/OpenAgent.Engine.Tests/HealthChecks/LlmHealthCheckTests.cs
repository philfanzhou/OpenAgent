using Microsoft.Extensions.Diagnostics.HealthChecks;
using Moq;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Models;
using OpenAgent.Engine.Redis;
using Xunit;

namespace OpenAgent.Engine.Tests.HealthChecks;

public class LlmHealthCheckTests
{
    [Fact]
    public async Task Returns_degraded_when_no_model_is_configured()
    {
        HealthCheckResult result = await CreateCheck().CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Degraded, result.Status);
    }

    [Theory]
    [InlineData("http://localhost:11434", HealthStatus.Healthy, 0)]
    [InlineData("not-a-uri", HealthStatus.Unhealthy, 1)]
    public async Task CheckHealthAsync_OnlyInlineModel_ReportsModelValidity(string endpoint, HealthStatus expected, int invalidCount)
    {
        var agents = new Mock<IAgentConfigRepository>();
        agents.Setup(item => item.ListAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(new[]
        {
            new AgentConfigEntity { TenantId = "tenant-a", AgentId = "agent-a", Config = new AgentConfig
            {
                TenantId = "tenant-a", OpenCode = new OpenCodeConfig { Model = new OpenCodeModelConfig
                { ModelId = "local", Endpoint = endpoint, ContextTokens = 4096 } }
            }}
        });
        HealthCheckResult result = await CreateCheck(agents.Object).CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(expected, result.Status);
        Assert.Equal(1, result.Data["inlineModelCount"]);
        Assert.Equal(invalidCount, result.Data["invalidInlineModelCount"]);
    }

    [Fact]
    public async Task Returns_unhealthy_when_provider_data_is_partially_invalid()
    {
        HealthCheckResult result = await CreateCheck(profiles: new[]
        {
            Profile("tenant-a", "primary", "https://llm.example.test/v1"),
            Profile("tenant-b", "broken", "not-a-uri")
        }).CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal(1, result.Data["invalidProviderCount"]);
    }

    [Fact]
    public async Task CheckHealthAsync_ValidProvidersAcrossTenants_ReturnsHealthyWithoutRequiringApiKeys()
    {
        HealthCheckResult result = await CreateCheck(profiles: new[]
        {
            Profile("tenant-a", "primary", "https://llm.example.test/v1"),
            Profile("tenant-b", "primary", "http://localhost:11434")
        }).CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal(2, result.Data["providerCount"]);
        Assert.Equal(2, result.Data["validProviderCount"]);
        Assert.Equal(0, result.Data["invalidProviderCount"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CheckHealthAsync_RepositoryFailure_DistinguishesFailureFromCancellation(bool cancelled)
    {
        using CancellationTokenSource cancellation = new();
        if (cancelled) cancellation.Cancel();
        var repository = new Mock<ILlmConfigRepository>();
        repository.Setup(item => item.ListAsync(null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(cancelled ? new OperationCanceledException(cancellation.Token) : new InvalidOperationException("read failed"));
        var agents = new Mock<IAgentConfigRepository>();
        var check = new LlmHealthCheck(repository.Object, agents.Object);

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

    private static LlmHealthCheck CreateCheck(IAgentConfigRepository? agents = null, LlmProviderProfile[]? profiles = null)
    {
        var repository = new Mock<ILlmConfigRepository>();
        repository.Setup(item => item.ListAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(profiles ?? Array.Empty<LlmProviderProfile>());
        if (agents is null)
        {
            var empty = new Mock<IAgentConfigRepository>();
            empty.Setup(item => item.ListAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<AgentConfigEntity>());
            agents = empty.Object;
        }
        return new LlmHealthCheck(repository.Object, agents);
    }

    private static LlmProviderProfile Profile(string tenant, string id, string endpoint) => new()
    {
        TenantId = tenant, Id = id, ModelId = "model", ContextTokens = 8192, Endpoint = endpoint
    };
}
