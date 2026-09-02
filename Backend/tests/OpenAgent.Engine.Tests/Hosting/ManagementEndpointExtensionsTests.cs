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

        Assert.Empty(redacted.Config.Llm.ApiKey);
    }

    [Fact]
    public void RedactLlm_RemovesProviderSecret()
    {
        var profile = new LlmProviderProfile
        {
            Id = "primary",
            ApiKey = "provider-secret"
        };

        LlmProviderProfile redacted = ManagementEndpointExtensions.RedactLlm(profile);

        Assert.Empty(redacted.ApiKey);
        Assert.Equal("provider-secret", profile.ApiKey);
    }
}
