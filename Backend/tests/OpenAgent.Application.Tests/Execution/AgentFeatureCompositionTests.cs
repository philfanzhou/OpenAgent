using OpenAgent.Contracts.Capabilities;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Runtime;
using OpenAgent.Contracts.Security;
using OpenAgent.Application.Tests.TestDoubles;
using OpenAgent.Application.Tooling.Abstractions;
using Xunit;

namespace OpenAgent.Application.Tests.Runtime;

public class AgentFeatureCompositionTests
{
    [Fact]
    public async Task ExecuteAsync_RegisteredFeature_ContributesToolAndReleasesItsResources()
    {
        TrackingResource resource = new();
        var driver = new ScriptedRuntimeDriver(
        [
            new AgentStreamEvent { Type = AgentStreamEventType.Content, Content = "done" }
        ]);
        await using AgentExecutorUsageTests.TestRuntime runtime = AgentExecutorUsageTests.CreateRuntime(
            driver,
            configure: services => services.AddSingleton<IAgentFeatureFactory>(new TestFeature(resource)));

        await runtime.Executor.ExecuteAsync(Request, User, CancellationToken.None);

        RuntimeInvocation invocation = Assert.Single(driver.Invocations);
        Assert.Contains(invocation.Tools, tool => tool.Name == "extension_tool");
        Assert.Equal(1, resource.DisposeCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_LaterFeatureFails_ReleasesEarlierFeaturesAndPreservesOriginalError(bool cleanupFails)
    {
        TrackingResource resource = new(cleanupFails);
        await using AgentExecutorUsageTests.TestRuntime runtime = AgentExecutorUsageTests.CreateRuntime(
            new UnreachableDriver(),
            configure: services =>
            {
                services.AddSingleton<IAgentFeatureFactory>(new TestFeature(resource));
                services.AddSingleton<IAgentFeatureFactory>(new FailedFeature());
            });

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.Executor.ExecuteAsync(Request, User, CancellationToken.None));

        Assert.Equal("feature failed", error.Message);
        Assert.Equal(1, resource.DisposeCount);
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

    private sealed class UnreachableDriver : IAgentRuntimeDriver
    {
        public IAsyncEnumerable<AgentStreamEvent> RunAsync(RuntimeInvocation invocation, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Failed preparation must not start the driver.");
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
