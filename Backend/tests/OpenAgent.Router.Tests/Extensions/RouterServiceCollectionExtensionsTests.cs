using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenAgent.Router.Options;
using Xunit;
using Yarp.ReverseProxy.Forwarder;

namespace OpenAgent.Router.Tests.Extensions;

public class RouterServiceCollectionExtensionsTests
{
    [Fact]
    public void AddRouterRuntime_ValidProviderConfiguration_BindsProviderAndIntentOptions()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(CreateValidSettings())
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRouterRuntime(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();

        AgentProviderOptions providers = provider
            .GetRequiredService<IOptions<AgentProviderOptions>>()
            .Value;
        IntentRecognitionOptions intent = provider
            .GetRequiredService<IOptions<IntentRecognitionOptions>>()
            .Value;

        Assert.Equal("self-engine", providers.DefaultProviderId);
        Assert.Equal("self-engine", Assert.Single(providers.Providers).Id);
        Assert.Equal("self-engine", intent.ProviderId);
        Assert.Equal("intent-router", intent.AgentId);
    }

    [Fact]
    public void AddRouterRuntime_DefaultConfiguration_ResolvesBuiltInProvider()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(CreateValidSettings())
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(configuration);
        services.AddHttpForwarder();
        services.AddRouterRuntime(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();

        IAgentProviderRegistry registry = provider
            .GetRequiredService<IAgentProviderRegistry>();

        Assert.Equal("self-engine", registry.DefaultProvider.Id);
        Assert.Single(registry.Providers);
    }

    [Theory]
    [InlineData("missing-default")]
    [InlineData("unknown-default")]
    [InlineData("duplicate-provider")]
    [InlineData("invalid-base-url")]
    public void AddRouterRuntime_InvalidProviderConfiguration_FailsValidation(string scenario)
    {
        Dictionary<string, string?> settings = CreateValidSettings();
        switch (scenario)
        {
            case "missing-default":
                settings.Remove("RouterSettings:AgentProviders:DefaultProviderId");
                break;
            case "unknown-default":
                settings["RouterSettings:AgentProviders:DefaultProviderId"] = "missing";
                break;
            case "duplicate-provider":
                settings["RouterSettings:AgentProviders:Providers:1:Id"] = "self-engine";
                break;
            case "invalid-base-url":
                settings["RouterSettings:AgentProviders:Providers:0:BaseUrl"] = "not-a-url";
                break;
        }

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRouterRuntime(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<AgentProviderOptions>>().Value);
    }

    internal static Dictionary<string, string?> CreateValidSettings() => new()
    {
        ["RouterSettings:IntentRecognition:Enabled"] = "true",
        ["RouterSettings:IntentRecognition:ProviderId"] = "self-engine",
        ["RouterSettings:IntentRecognition:AgentId"] = "intent-router",
        ["RouterSettings:AgentProviders:DefaultProviderId"] = "self-engine",
        ["RouterSettings:AgentProviders:Providers:0:Id"] = "self-engine"
    };
}
