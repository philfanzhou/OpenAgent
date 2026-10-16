using OpenAgent.Router.Options;
using OpenAgent.Router.Providers;
using Xunit;

namespace OpenAgent.Router.Tests.Providers;

public class AgentProviderRegistryTests
{
    [Fact]
    public void Constructor_BuildsOneHttpProviderPerDefinition()
    {
        AgentProviderOptions options = new()
        {
            DefaultProviderId = "partner",
            Providers =
            [
                new AgentProviderDefinition { Id = "self-engine" },
                new AgentProviderDefinition { Id = "partner", BaseUrl = "https://partner.example" }
            ]
        };

        using var registry = new AgentProviderRegistry(
            Microsoft.Extensions.Options.Options.Create(options),
            new StubRouteTable());

        Assert.Equal("partner", registry.DefaultProvider.Id);
        Assert.Equal(2, registry.Providers.Count);
        Assert.True(registry.TryGet("SELF-ENGINE", out IAgentProvider? provider));
        Assert.IsType<HttpProvider>(provider);
    }

    private sealed class StubRouteTable : IRouteTable
    {
        public string? GetTargetEndpoint(string intent) => null;
    }
}
