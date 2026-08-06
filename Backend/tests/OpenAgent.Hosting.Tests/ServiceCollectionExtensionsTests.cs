using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenAgent.Hosting.Authentication;
using StackExchange.Redis;
using Xunit;

namespace OpenAgent.Hosting.Tests;

public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddAgentHost_RegistersConfiguredOptions()
    {
        using var provider = CreateServices(options =>
        {
            DisableOptionalFeatures(options);
            options.ServiceName = "test-host";
            options.ServiceVersion = "2.0.0";
            options.HealthCheckLivePath = "/live";
        }).BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<AgentHostOptions>>().Value;

        Assert.Equal("test-host", options.ServiceName);
        Assert.Equal("2.0.0", options.ServiceVersion);
        Assert.Equal("/live", options.HealthCheckLivePath);
    }

    [Fact]
    public async Task AddAgentHost_WithJwtAuthEnabled_RegistersPassThroughScheme()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddAgentHost(configuration, options =>
        {
            DisableOptionalFeatures(options);
            options.EnableJwtAuth = true;
        });

        using var provider = services.BuildServiceProvider();
        var schemes = provider.GetRequiredService<IAuthenticationSchemeProvider>();

        Assert.NotNull(await schemes.GetSchemeAsync("PassThrough"));
    }

    [Fact]
    public async Task AddAgentHost_WithJwtBearerMode_RegistersBearerScheme()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Mode"] = "JwtBearer",
                ["Authentication:Authority"] = "https://identity.example.com",
                ["Authentication:Audience"] = "openagent-engine"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddAgentHost(configuration, options =>
        {
            DisableOptionalFeatures(options);
            options.EnableJwtAuth = true;
        });

        using var provider = services.BuildServiceProvider();
        var schemes = provider.GetRequiredService<IAuthenticationSchemeProvider>();

        Assert.NotNull(await schemes.GetSchemeAsync("Bearer"));
        Assert.Equal(AgentAuthenticationMode.JwtBearer,
            provider.GetRequiredService<IOptions<AgentAuthenticationOptions>>().Value.Mode);
    }

    [Fact]
    public async Task AddAgentHost_WithApiKeyMode_RegistersApiKeyScheme()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Mode"] = "ApiKey",
                ["Authentication:ApiKeys:test-key:UserId"] = "service-user"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddAgentHost(configuration, options =>
        {
            DisableOptionalFeatures(options);
            options.EnableJwtAuth = true;
        });

        using var provider = services.BuildServiceProvider();
        var schemes = provider.GetRequiredService<IAuthenticationSchemeProvider>();

        Assert.NotNull(await schemes.GetSchemeAsync("ApiKey"));
    }

    [Fact]
    public void AddAgentHost_DoesNotRegisterConnectionMultiplexer()
    {
        // Hosting no longer registers IConnectionMultiplexer — that is now owned by Agent.Core.
        using var provider = CreateServices(DisableOptionalFeatures).BuildServiceProvider();

        Assert.Null(provider.GetService<IConnectionMultiplexer>());
    }

    [Fact]
    public void AddAgentHost_WithOptionalFeaturesDisabled_RemainsResolvable()
    {
        using var provider = CreateServices(DisableOptionalFeatures).BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<AgentHostOptions>>().Value;
        Assert.False(options.EnableCors);
        Assert.False(options.EnableSwagger);
        Assert.False(options.EnableHealthChecks);
        Assert.False(options.EnableJwtAuth);
        Assert.False(options.EnableOpenTelemetry);
    }

    private static ServiceCollection CreateServices(Action<AgentHostOptions> configure)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var configuration = new ConfigurationBuilder().Build();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddAgentHost(configuration, configure);
        return services;
    }

    private static void DisableOptionalFeatures(AgentHostOptions options)
    {
        options.EnableCors = false;
        options.EnableSwagger = false;
        options.EnableHealthChecks = false;
        options.EnableJwtAuth = false;
        options.EnableOpenTelemetry = false;
    }
}
