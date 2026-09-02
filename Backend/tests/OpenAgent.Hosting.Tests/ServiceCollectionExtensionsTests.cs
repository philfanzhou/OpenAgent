using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenAgent.Hosting.Authentication;
using StackExchange.Redis;
using Xunit;

namespace OpenAgent.Hosting.Tests;

public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AgentHostOptions_DefaultServiceNameUsesOpenAgentPrefix()
    {
        Assert.Equal("openagent-service", new AgentHostOptions().ServiceName);
    }

    [Fact]
    public void AddAgentHost_RegistersConfiguredOptions()
    {
        using var provider = CreateServices(options =>
        {
            DisableOptionalFeatures(options);
            options.ServiceName = "test-host";
            options.ServiceVersion = "2.0.0";
            options.OpenTelemetrySource = "Test.Host";
            options.HealthCheckLivePath = "/live";
        }).BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<AgentHostOptions>>().Value;

        Assert.Equal("test-host", options.ServiceName);
        Assert.Equal("2.0.0", options.ServiceVersion);
        Assert.Equal("Test.Host", options.OpenTelemetrySource);
        Assert.Equal("/live", options.HealthCheckLivePath);
    }

    [Fact]
    public async Task AddAgentHost_WithJwtAuthEnabled_RegistersBasicScheme()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Mode"] = "Basic"
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

        Assert.NotNull(await schemes.GetSchemeAsync("Basic"));
    }

    [Fact]
    public async Task AddAgentHost_WithJwtBearerMode_RegistersBearerScheme()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Mode"] = "JwtBearer",
                ["Authentication:Authority"] = "https://identity.example",
                ["Authentication:MetadataAddress"] = "http://keycloak:8080/realms/openagent/.well-known/openid-configuration",
                ["Authentication:Audience"] = "openagent-api",
                ["Authentication:ClientId"] = "openagent-chat"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddAgentHost(configuration, options =>
        {
            DisableOptionalFeatures(options);
            options.EnableJwtAuth = true;
        });

        using ServiceProvider provider = services.BuildServiceProvider();
        var schemes = provider.GetRequiredService<IAuthenticationSchemeProvider>();

        Assert.NotNull(await schemes.GetSchemeAsync("Bearer"));
        Assert.Null(await schemes.GetSchemeAsync("Basic"));
        Assert.Equal(
            "http://keycloak:8080/realms/openagent/.well-known/openid-configuration",
            provider.GetRequiredService<IOptions<AgentAuthenticationOptions>>().Value.MetadataAddress);
    }

    [Fact]
    public async Task AddAgentHost_WithApiKeyMode_RegistersApiKeyScheme()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Mode"] = "ApiKey",
                ["Authentication:ApiKeyHash"] = new string('a', 64),
                ["Authentication:ApiKeyTenantId"] = "tenant-a",
                ["Authentication:ApiKeyClientId"] = "partner-a"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddAgentHost(configuration, options =>
        {
            DisableOptionalFeatures(options);
            options.EnableJwtAuth = true;
        });

        using ServiceProvider provider = services.BuildServiceProvider();
        var schemes = provider.GetRequiredService<IAuthenticationSchemeProvider>();

        Assert.NotNull(await schemes.GetSchemeAsync("ApiKey"));
        Assert.Null(await schemes.GetSchemeAsync("Bearer"));
        Assert.Null(await schemes.GetSchemeAsync("Basic"));
    }

    [Fact]
    public void AddAgentHost_WithIncompleteApiKeyConfiguration_FailsValidation()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Mode"] = "ApiKey",
                ["Authentication:ApiKeyTenantId"] = "tenant-a"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentHost(configuration, options =>
        {
            DisableOptionalFeatures(options);
            options.EnableJwtAuth = true;
        });

        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<AgentAuthenticationOptions>>().Value);
    }

    [Fact]
    public void AddAgentHost_WithIncompleteJwtBearerConfiguration_FailsValidation()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Mode"] = "JwtBearer"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentHost(configuration, options =>
        {
            DisableOptionalFeatures(options);
            options.EnableJwtAuth = true;
        });

        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<AgentAuthenticationOptions>>().Value);
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

    [Theory]
    [InlineData("not-an-absolute-uri")]
    [InlineData("ftp://collector.example.com")]
    public void AddAgentHost_WithInvalidOtlpEndpoint_Throws(string endpoint)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenTelemetry:OtlpEndpoint"] = endpoint
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddAgentHost(configuration, options =>
            {
                DisableOptionalFeatures(options);
                options.EnableOpenTelemetry = true;
            }));

        Assert.Contains("absolute HTTP(S) URI", exception.Message, StringComparison.Ordinal);
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
