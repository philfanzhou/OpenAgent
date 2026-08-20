using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Runtime.Agent;
using Xunit;

namespace OpenAgent.Core.Tests.Runtime;

public sealed class AgentModelResolverTests
{
    [Fact]
    public async Task ResolveAsync_MessageOverride_TakesPrecedenceOverConversationAndAgent()
    {
        var runtime = new RecordingRuntimeResolver();
        var resolver = new AgentModelResolver(runtime);
        var request = new AgentRequest
        {
            Query = "hello",
            MessageModelOverride = Model("message-provider", "message-model"),
            ConversationModelOverride = Model("request-provider", "request-model"),
            UpdateConversationModelOverride = true
        };

        AgentModelResolution result = await resolver.ResolveAsync(
            "agent-1",
            request,
            Model("stored-provider", "stored-model"),
            User(),
            CancellationToken.None);

        Assert.Equal("message-model", result.Profile.Model.ModelId);
        Assert.Equal(LlmModelSelectionSource.Message, result.Source);
        Assert.False(result.ApplyConversationUpdate);
    }

    [Fact]
    public async Task ResolveAsync_ConversationOverride_TakesPrecedenceOverPersistedAndAgent()
    {
        var resolver = new AgentModelResolver(new RecordingRuntimeResolver());
        var request = new AgentRequest
        {
            Query = "hello",
            ConversationModelOverride = Model("request-provider", "request-model"),
            UpdateConversationModelOverride = true
        };

        AgentModelResolution result = await resolver.ResolveAsync(
            "agent-1",
            request,
            Model("stored-provider", "stored-model"),
            User(),
            CancellationToken.None);

        Assert.Equal("request-model", result.Profile.Model.ModelId);
        Assert.Equal(LlmModelSelectionSource.Conversation, result.Source);
        Assert.True(result.ApplyConversationUpdate);
        Assert.Equal("request-model", result.ConversationModel?.ModelId);
    }

    [Fact]
    public async Task ResolveAsync_InvalidPersistedOverride_FallsBackAndClearsConversationModel()
    {
        var resolver = new AgentModelResolver(new RecordingRuntimeResolver("removed-provider"));

        AgentModelResolution result = await resolver.ResolveAsync(
            "agent-1",
            new AgentRequest { Query = "hello" },
            Model("removed-provider", "removed-model"),
            User(),
            CancellationToken.None);

        Assert.Equal("agent-default", result.Profile.Model.ModelId);
        Assert.Equal(LlmModelSelectionSource.AgentFallback, result.Source);
        Assert.True(result.ApplyConversationUpdate);
        Assert.Null(result.ConversationModel);
    }

    [Fact]
    public async Task ResolveAsync_InvalidMessageOverride_ReturnsClearErrorWithoutFallback()
    {
        var resolver = new AgentModelResolver(new RecordingRuntimeResolver("removed-provider"));
        var request = new AgentRequest
        {
            Query = "hello",
            MessageModelOverride = Model("removed-provider", "removed-model")
        };

        AgentException exception = await Assert.ThrowsAsync<AgentException>(() => resolver.ResolveAsync(
            "agent-1",
            request,
            persistedConversationModel: null,
            User(),
            CancellationToken.None));

        Assert.Equal(AgentErrorCode.LlmModelNotFound, exception.ErrorCode);
    }

    private static LlmModelSelection Model(string provider, string modelId) => new()
    {
        Provider = provider,
        ModelId = modelId
    };

    private static AgentUserContext User() => new()
    {
        UserId = "user-1",
        TenantId = "tenant-1",
        IsAuthenticated = true
    };

    private sealed class RecordingRuntimeResolver(string? invalidProvider = null) : IAgentRuntimeResolver
    {
        public Task<AgentRuntimeProfile> ResolveAsync(
            string agentId,
            IAgentUserContext userContext,
            CancellationToken cancellationToken = default) =>
            ResolveAsync(agentId, userContext, null, cancellationToken);

        public Task<AgentRuntimeProfile> ResolveAsync(
            string agentId,
            IAgentUserContext userContext,
            LlmModelSelection? modelOverride,
            CancellationToken cancellationToken = default)
        {
            if (string.Equals(modelOverride?.Provider, invalidProvider, StringComparison.Ordinal))
            {
                throw new AgentException(
                    AgentErrorCode.LlmModelNotFound,
                    "The selected model is no longer available.");
            }

            return Task.FromResult(new AgentRuntimeProfile
            {
                AgentId = agentId,
                Config = new AgentConfig(),
                Model = new LlmConfig
                {
                    Provider = modelOverride?.Provider ?? "agent-provider",
                    ModelId = modelOverride?.ModelId ?? "agent-default"
                }
            });
        }
    }
}
