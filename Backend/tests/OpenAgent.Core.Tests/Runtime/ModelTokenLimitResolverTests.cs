using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Runtime.Agent;
using Xunit;

namespace OpenAgent.Core.Tests.Runtime;

public sealed class ModelTokenLimitResolverTests
{
    [Fact]
    public void Apply_RequestValuesOverrideAgentDefaultsWithinModelCapabilities()
    {
        AgentRuntimeProfile profile = Profile(
            agentContext: 64_000,
            agentOutput: 4_000,
            modelContext: 128_000,
            modelOutput: 16_000);

        AgentRuntimeProfile result = ModelTokenLimitResolver.Apply(profile, new AgentRequest
        {
            Query = "hello",
            ContextWindowTokens = 96_000,
            MaxOutputTokens = 8_000
        });

        Assert.Equal(96_000, result.Model.ContextTokens);
        Assert.Equal(8_000, result.Model.MaxOutputTokens);
        Assert.Equal(64_000, profile.Model.ContextTokens);
        Assert.Equal(4_000, profile.Model.MaxOutputTokens);
        Assert.Equal(ModelModality.Multimodal, result.Model.Modality);
    }

    [Theory]
    [InlineData(128_001, null, "context window")]
    [InlineData(null, 16_001, "maximum output")]
    [InlineData(0, null, "positive")]
    [InlineData(null, -1, "positive")]
    public void Apply_RequestOutsideBoundaries_ThrowsInvalidRequest(
        int? contextWindowTokens,
        int? maxOutputTokens,
        string expectedMessage)
    {
        AgentRuntimeProfile profile = Profile(
            agentContext: 64_000,
            agentOutput: 4_000,
            modelContext: 128_000,
            modelOutput: 16_000);

        AgentException exception = Assert.Throws<AgentException>(() =>
            ModelTokenLimitResolver.Apply(profile, new AgentRequest
            {
                Query = "hello",
                ContextWindowTokens = contextWindowTokens,
                MaxOutputTokens = maxOutputTokens
            }));

        Assert.Equal(AgentErrorCode.InvalidRequest, exception.ErrorCode);
        Assert.Contains(expectedMessage, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Apply_ProviderDoesNotSupportRequestParameter_ThrowsBeforeProviderCall()
    {
        AgentRuntimeProfile profile = Profile(
            agentContext: 64_000,
            agentOutput: 4_000,
            modelContext: 128_000,
            modelOutput: 16_000,
            supportsMaxOutputTokens: false);

        AgentException exception = Assert.Throws<AgentException>(() =>
            ModelTokenLimitResolver.Apply(profile, new AgentRequest
            {
                Query = "hello",
                MaxOutputTokens = 2_000
            }));

        Assert.Equal(AgentErrorCode.InvalidRequest, exception.ErrorCode);
        Assert.Contains("does not support", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Apply_OutputReservationConsumesContext_ThrowsBeforeProviderCall()
    {
        AgentRuntimeProfile profile = Profile(
            agentContext: 64_000,
            agentOutput: 4_000,
            modelContext: 128_000,
            modelOutput: 16_000);

        AgentException exception = Assert.Throws<AgentException>(() =>
            ModelTokenLimitResolver.Apply(profile, new AgentRequest
            {
                Query = "hello",
                ContextWindowTokens = 8_000,
                MaxOutputTokens = 8_000
            }));

        Assert.Equal(AgentErrorCode.InvalidRequest, exception.ErrorCode);
        Assert.Contains("less than", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static AgentRuntimeProfile Profile(
        int agentContext,
        int agentOutput,
        int modelContext,
        int modelOutput,
        bool supportsMaxOutputTokens = true) => new()
        {
            AgentId = "agent-1",
            Config = new AgentConfig
            {
                ContextWindowTokens = agentContext,
                MaxOutputTokens = agentOutput
            },
            Model = new LlmConfig
            {
                ModelId = "model-1",
                Modality = ModelModality.Multimodal,
                ContextTokens = agentContext,
                MaxOutputTokens = agentOutput,
                TokenCapabilities = new LlmTokenCapabilities
                {
                    ContextWindowTokens = modelContext,
                    MaxOutputTokens = modelOutput,
                    SupportsMaxOutputTokens = supportsMaxOutputTokens
                }
            }
        };
}
