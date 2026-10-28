using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Moq;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Engine.Redis;
using Xunit;

namespace OpenAgent.Engine.Tests.HealthChecks;

public class LlmHealthCheckTests
{
    private const string DefaultTenantId = "tenant-default";

    [Fact]
    public async Task Returns_degraded_when_no_provider_is_configured()
    {
        var repository = new Mock<ILlmConfigRepository>();
        repository.Setup(item => item.ListAsync(DefaultTenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<LlmProviderProfile>());

        HealthCheckResult result = await new LlmHealthCheck(repository.Object, Configuration())
            .CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Equal("No LLM provider is configured.", result.Description);
    }

    [Fact]
    public async Task Returns_healthy_when_a_valid_provider_is_configured()
    {
        var repository = new Mock<ILlmConfigRepository>();
        repository.Setup(item => item.ListAsync(DefaultTenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new LlmProviderProfile
                {
                    Id = "primary",
                    Endpoint = "https://llm.example.test/v1",
                    ModelId = "model",
                    ContextTokens = 8192
                }
            });

        HealthCheckResult result = await new LlmHealthCheck(repository.Object, Configuration())
            .CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal(1, result.Data["providerCount"]);
    }

    [Fact]
    public async Task Returns_unhealthy_when_provider_configuration_cannot_be_read()
    {
        var repository = new Mock<ILlmConfigRepository>();
        repository.Setup(item => item.ListAsync(DefaultTenantId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database unavailable"));

        HealthCheckResult result = await new LlmHealthCheck(repository.Object, Configuration())
            .CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.NotNull(result.Exception);
    }

    private static IConfiguration Configuration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Authentication:DevelopmentTenantId"] = DefaultTenantId
        })
        .Build();
}
