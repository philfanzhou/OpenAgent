using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Models;
using OpenAgent.Engine.Host.Controllers;
using Xunit;

namespace OpenAgent.Engine.Tests.Hosting;

public class ManagementEndpointExtensionsTests
{
    [Fact]
    public void Redact_RemovesEmbeddedRagSecret()
    {
        var entity = new AgentConfigEntity
        {
            AgentId = "finance",
            Config = new AgentConfig
            {
                Rag = new RagConfig
                {
                    Instances =
                    [
                        new RagInstanceConfig
                        {
                            Id = "knowledge",
                            ApiKey = "rag-secret",
                            ApiKeySecretRef = "rag:knowledge"
                        }
                    ]
                }
            }
        };

        AgentConfigEntity redacted = ConfigurationRedactor.Redact(entity);

        RagInstanceConfig rag = Assert.Single(redacted.Config.Rag.Instances);
        Assert.Empty(rag.ApiKey);
        Assert.Equal("rag:knowledge", rag.ApiKeySecretRef);
    }

    [Fact]
    public void RedactLlm_RemovesProviderSecret()
    {
        var profile = new LlmProviderProfile
        {
            Id = "primary",
            ApiKey = "provider-secret",
            Modality = ModelModality.Multimodal
        };

        LlmProviderProfile redacted = ConfigurationRedactor.RedactLlm(profile);

        Assert.Empty(redacted.ApiKey);
        Assert.Equal(ModelModality.Multimodal, redacted.Modality);
        Assert.Equal("provider-secret", profile.ApiKey);
    }
}
