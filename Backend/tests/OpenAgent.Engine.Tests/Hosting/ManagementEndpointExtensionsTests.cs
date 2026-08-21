using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Models;
using OpenAgent.Engine.Host.Extensions;
using Xunit;

namespace OpenAgent.Engine.Tests.Hosting;

public class ManagementEndpointExtensionsTests
{
    [Fact]
    public void Redact_RemovesEmbeddedLlmSecret()
    {
        var entity = new AgentConfigEntity
        {
            AgentId = "finance",
            Config = new AgentConfig
            {
                Llm = new LlmConfig { ApiKey = "llm-secret" }
            }
        };

        AgentConfigEntity redacted = ManagementEndpointExtensions.Redact(entity);

        Assert.Equal("***", redacted.Config.Llm.ApiKey);
    }

    [Fact]
    public void RedactLlm_RemovesProviderSecret()
    {
        var profile = new LlmProviderProfile
        {
            Id = "primary",
            ApiKey = "provider-secret",
            ContextWindowTokens = 128_000,
            MaxOutputTokens = 16_000,
            SupportsMaxOutputTokens = false
        };

        LlmProviderProfile redacted = ManagementEndpointExtensions.RedactLlm(profile);

        Assert.Equal("***", redacted.ApiKey);
        Assert.Equal(128_000, redacted.ContextWindowTokens);
        Assert.Equal(16_000, redacted.MaxOutputTokens);
        Assert.False(redacted.SupportsMaxOutputTokens);
        Assert.Equal("provider-secret", profile.ApiKey);
    }

    [Theory]
    [InlineData(0, null, "positive")]
    [InlineData(null, -1, "positive")]
    [InlineData(4_000, 4_000, "less than")]
    [InlineData(4_000, 5_000, "less than")]
    public void ValidateLlmTokenCapabilities_InvalidLimits_ReturnsError(
        int? contextWindowTokens,
        int? maxOutputTokens,
        string expectedMessage)
    {
        var profile = new LlmProviderProfile
        {
            ContextWindowTokens = contextWindowTokens,
            MaxOutputTokens = maxOutputTokens
        };

        string? error = ManagementEndpointExtensions.ValidateLlmTokenCapabilities(profile);

        Assert.Contains(expectedMessage, error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateLlmTokenCapabilities_ValidLimits_ReturnsNoError()
    {
        var profile = new LlmProviderProfile
        {
            ContextWindowTokens = 128_000,
            MaxOutputTokens = 16_000
        };

        string? error = ManagementEndpointExtensions.ValidateLlmTokenCapabilities(profile);

        Assert.Null(error);
    }
}
