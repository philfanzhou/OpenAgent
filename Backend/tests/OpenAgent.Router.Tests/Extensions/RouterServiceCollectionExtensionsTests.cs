using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenAgent.Router.Options;
using Xunit;

namespace OpenAgent.Router.Tests.Extensions;

public class RouterServiceCollectionExtensionsTests
{
    [Fact]
    public void AddRouterRuntime_ValidExternalAgentConfiguration_ResolvesOptions()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(CreateValidSettings())
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRouterRuntime(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();

        ExternalAgentRoutingOptions options = provider
            .GetRequiredService<IOptions<ExternalAgentRoutingOptions>>()
            .Value;

        ExternalAgentOptions agent = Assert.Single(options.Agents);
        Assert.Equal("external-support", agent.AgentId);
    }

    [Theory]
    [InlineData("duplicate-id")]
    [InlineData("invalid-endpoint")]
    [InlineData("invalid-header")]
    [InlineData("unknown-adapter")]
    public void AddRouterRuntime_InvalidExternalAgentConfiguration_FailsValidation(string scenario)
    {
        Dictionary<string, string?> settings = CreateValidSettings();
        switch (scenario)
        {
            case "duplicate-id":
                settings["RouterSettings:ExternalAgents:Agents:1:AgentId"] = "external-support";
                settings["RouterSettings:ExternalAgents:Agents:1:BaseUrl"] = "https://second.example";
                break;
            case "invalid-endpoint":
                settings["RouterSettings:ExternalAgents:Agents:0:BaseUrl"] = "file:///tmp/agent";
                break;
            case "invalid-header":
                settings["RouterSettings:ExternalAgents:Agents:0:Authentication:HeaderName"] = "Bad Header";
                break;
            case "unknown-adapter":
                settings["RouterSettings:ExternalAgents:Agents:0:Adapter"] = "Unknown";
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
            provider.GetRequiredService<IOptions<ExternalAgentRoutingOptions>>().Value);
    }

    private static Dictionary<string, string?> CreateValidSettings() => new()
    {
        ["RouterSettings:IntentRecognition:Enabled"] = "true",
        ["RouterSettings:IntentRecognition:AgentId"] = "intent-router",
        ["RouterSettings:ExternalAgents:Agents:0:AgentId"] = "external-support",
        ["RouterSettings:ExternalAgents:Agents:0:BaseUrl"] = "https://partner.example",
        ["RouterSettings:ExternalAgents:Agents:0:Authentication:HeaderName"] = "Authorization"
    };
}
