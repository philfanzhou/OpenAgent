using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Tests.TestDoubles;
using OpenAgent.Core.Tooling.Abstractions;
using Xunit;

namespace OpenAgent.Core.Tests.Runtime;

public class AgentFeatureCompositionTests
{
    [Fact]
    public async Task ExecuteAsync_RegisteredFeature_ContributesToolAndReleasesItsResources()
    {
        TrackingResource resource = new();
        FakeChatProvider provider = new(new Microsoft.Extensions.AI.ChatResponse(new ChatMessage(ChatRole.Assistant, "done")));
        await using AgentExecutorUsageTests.TestRuntime runtime = AgentExecutorUsageTests.CreateRuntime(
            provider,
            configure: services => services.AddSingleton<IAgentFeatureFactory>(new TestFeature(resource)));

        await runtime.Executor.ExecuteAsync(Request, User, CancellationToken.None);

        Assert.Contains(provider.LastOptions!.Tools!, tool => tool.Name == "extension_tool");
        Assert.Equal(1, resource.DisposeCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_LaterFeatureFails_ReleasesEarlierFeaturesAndPreservesOriginalError(bool cleanupFails)
    {
        TrackingResource resource = new(cleanupFails);
        FakeChatProvider provider = new(new Microsoft.Extensions.AI.ChatResponse(new ChatMessage(ChatRole.Assistant, "unused")));
        await using AgentExecutorUsageTests.TestRuntime runtime = AgentExecutorUsageTests.CreateRuntime(
            provider,
            configure: services =>
            {
                services.AddSingleton<IAgentFeatureFactory>(new TestFeature(resource));
                services.AddSingleton<IAgentFeatureFactory>(new FailedFeature());
            });

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.Executor.ExecuteAsync(Request, User, CancellationToken.None));

        Assert.Equal("feature failed", error.Message);
        Assert.Equal(1, resource.DisposeCount);
        Assert.Null(provider.LastOptions);
    }

    private static AgentRequest Request => new()
    {
        Query = "hello", AgentId = "test-agent", LlmProfileId = "test-model",
        ConversationId = "feature-test", TraceId = "feature-trace"
    };

    private static AgentUserContext User => new() { UserId = "user-1", TenantId = "tenant-1" };

    private sealed class TestFeature(TrackingResource resource) : IAgentFeatureFactory
    {
        public Task<AgentFeature> CreateAsync(string agentId, AgentConfig config, IAgentUserContext user,
            CancellationToken cancellationToken) => Task.FromResult(new AgentFeature
            {
                Tools = [AIFunctionFactory.Create(() => "extension result", name: "extension_tool")],
                Resource = resource
            });
    }

    private sealed class FailedFeature : IAgentFeatureFactory
    {
        public Task<AgentFeature> CreateAsync(string agentId, AgentConfig config, IAgentUserContext user,
            CancellationToken cancellationToken) => throw new InvalidOperationException("feature failed");
    }

    private sealed class TrackingResource(bool fail = false) : IAsyncDisposable
    {
        internal int DisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            if (fail) throw new InvalidOperationException("cleanup failed");
            return ValueTask.CompletedTask;
        }
    }
}
