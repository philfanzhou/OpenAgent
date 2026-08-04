using OpenAgent.Contracts.Configuration;
using OpenAgent.Core.Models;
using Xunit;

namespace OpenAgent.Core.Tests.Models;

public class LlmRegistryTests
{
    [Fact]
    public void Register_EmptyId_IsIgnored()
    {
        var registry = new LlmRegistry();
        registry.Register(new LlmProviderProfile { Id = "", Name = "x" });
        Assert.Empty(registry.GetAllProfiles());
    }

    [Fact]
    public void Register_NullId_IsIgnored()
    {
        var registry = new LlmRegistry();
        registry.Register(new LlmProviderProfile { Id = null!, Name = "x" });
        Assert.Empty(registry.GetAllProfiles());
    }

    [Fact]
    public void Register_OverwritesExistingProfileWithSameId()
    {
        var registry = new LlmRegistry();
        registry.Register(new LlmProviderProfile { Id = "p1", Endpoint = "http://old" });
        registry.Register(new LlmProviderProfile { Id = "p1", Endpoint = "http://new" });

        var profile = registry.GetProfile("p1");
        Assert.Single(registry.GetAllProfiles());
        Assert.Equal("http://new", profile!.Endpoint);
    }

    [Fact]
    public void GetProfile_MissingId_ReturnsNull()
    {
        var registry = new LlmRegistry();
        Assert.Null(registry.GetProfile("does-not-exist"));
    }

    [Fact]
    public void ResolveConfig_WithoutProvider_ReturnsConfigUnchanged()
    {
        var registry = new LlmRegistry();
        var config = new LlmConfig
        {
            Provider = "",
            ModelId = "gpt-4o",
            Endpoint = "http://direct",
            ApiKey = "sk-direct"
        };

        var resolved = registry.ResolveConfig(config);

        Assert.Same(config, resolved);
        Assert.Equal("gpt-4o", resolved.ModelId);
        Assert.Equal("http://direct", resolved.Endpoint);
    }

    [Fact]
    public void ResolveConfig_WithProvider_FillsEndpointAndKeyFromProfile()
    {
        var registry = new LlmRegistry();
        registry.Register(new LlmProviderProfile
        {
            Id = "azure",
            Format = ApiFormat.OpenAIChatCompletions,
            Endpoint = "https://azure.example.com",
            ApiKey = "sk-azure"
        });

        var resolved = registry.ResolveConfig(new LlmConfig
        {
            Provider = "azure",
            ModelId = "gpt-4o",
            Temperature = 0.5
        });

        Assert.Equal("azure", resolved.Provider);
        Assert.Equal(ApiFormat.OpenAIChatCompletions, resolved.Format);
        Assert.Equal("https://azure.example.com", resolved.Endpoint);
        Assert.Equal("sk-azure", resolved.ApiKey);
        Assert.Equal("gpt-4o", resolved.ModelId);
        Assert.Equal(0.5, resolved.Temperature);
    }

    [Fact]
    public void ResolveConfig_UnknownProvider_Throws()
    {
        var registry = new LlmRegistry();
        Assert.Throws<InvalidOperationException>(
            () => registry.ResolveConfig(new LlmConfig { Provider = "missing" }));
    }
}
