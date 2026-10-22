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

    [Fact]
    public async Task ExecuteAsync_AsyncFactoryFails_ReleasesLateSucceedingFeatureExactlyOnce()
    {
        TrackingResource failing = new();
        TrackingResource lateSucceeding = new();
        await using AgentExecutorUsageTests.TestRuntime runtime = AgentExecutorUsageTests.CreateRuntime(
            new UnreachableDriver(),
            configure: services =>
            {
                // 快速失败：工厂在产出 feature 前抛错，平台无从接管其资源（工厂自负其责）。
                services.AddSingleton<IAgentFeatureFactory>(new DelayedFeature(failing, TimeSpan.FromMilliseconds(5), succeed: false));
                // 慢速成功：在失败点之后才完成，必须被失败路径接管并恰好释放一次。
                services.AddSingleton<IAgentFeatureFactory>(new DelayedFeature(lateSucceeding, TimeSpan.FromMilliseconds(120), succeed: true));
            });

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.Executor.ExecuteAsync(Request, User, CancellationToken.None));

        Assert.Equal("delayed feature failed", error.Message);
        Assert.Equal(0, failing.DisposeCount);
        Assert.Equal(1, lateSucceeding.DisposeCount);
    }

    [Fact]
    public async Task ExecuteAsync_ContextProviderFails_ReleasesFeatureExactlyOnce()
    {
        TrackingResource resource = new();
        await using AgentExecutorUsageTests.TestRuntime runtime = AgentExecutorUsageTests.CreateRuntime(
            new UnreachableDriver(),
            configure: services => services.AddSingleton<IAgentFeatureFactory>(new TestFeature(resource, new ThrowingProvider())));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.Executor.ExecuteAsync(Request, User, CancellationToken.None));

        Assert.Equal("provider failed", error.Message);
        Assert.Equal(1, resource.DisposeCount);
    }

    [Fact]
    public async Task ExecuteAsync_PrepareToolFails_ReleasesEveryFeatureExactlyOnce()
    {
        TrackingResource first = new();
        TrackingResource second = new();
        await using AgentExecutorUsageTests.TestRuntime runtime = AgentExecutorUsageTests.CreateRuntime(
            new UnreachableDriver(),
            configure: services =>
            {
                services.AddSingleton<IAgentFeatureFactory>(new TestFeature(first, prepareTool: _ => throw new InvalidOperationException("prepare failed")));
                services.AddSingleton<IAgentFeatureFactory>(new TestFeature(second));
            });

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.Executor.ExecuteAsync(Request, User, CancellationToken.None));

        Assert.Equal("prepare failed", error.Message);
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, second.DisposeCount);
    }

    [Fact]
    public async Task ExecuteAsync_DuplicateToolName_ReleasesEveryFeatureExactlyOnce()
    {
        TrackingResource first = new();
        TrackingResource second = new();
        await using AgentExecutorUsageTests.TestRuntime runtime = AgentExecutorUsageTests.CreateRuntime(
            new UnreachableDriver(),
            configure: services =>
            {
                services.AddSingleton<IAgentFeatureFactory>(new TestFeature(first, toolName: "dup_tool"));
                services.AddSingleton<IAgentFeatureFactory>(new TestFeature(second, toolName: "dup_tool"));
            });

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.Executor.ExecuteAsync(Request, User, CancellationToken.None));

        Assert.Contains("Duplicate runtime tool name", error.Message);
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, second.DisposeCount);
    }

    private static AgentRequest Request => new()
    {
        Query = "hello", AgentId = "test-agent", LlmProfileId = "test-model",
        ConversationId = "feature-test", TraceId = "feature-trace"
    };

    private static AgentUserContext User => new() { UserId = "user-1", TenantId = "tenant-1" };

    private sealed class TestFeature(TrackingResource resource, IAgentContextProvider? provider = null, string toolName = "extension_tool", Func<AITool, AITool>? prepareTool = null) : IAgentFeatureFactory
    {
        public Task<AgentFeature> CreateAsync(string agentId, AgentConfig config, IAgentUserContext user,
            CancellationToken cancellationToken) => Task.FromResult(new AgentFeature
        {
            Tools = [AIFunctionFactory.Create(() => "extension result", name: toolName)],
            Resource = resource,
            ContextProviders = provider is null ? [] : [provider],
            PrepareTool = prepareTool
        });
    }

    private sealed class ThrowingProvider : IAgentContextProvider
    {
        public Task<AgentTurnContext> InvokingAsync(AgentTurnContext context, CancellationToken cancellationToken)
            => throw new InvalidOperationException("provider failed");
    }

    private sealed class DelayedFeature(TrackingResource resource, TimeSpan delay, bool succeed) : IAgentFeatureFactory
    {
        public async Task<AgentFeature> CreateAsync(string agentId, AgentConfig config, IAgentUserContext user,
            CancellationToken cancellationToken)
        {
            await Task.Delay(delay).ConfigureAwait(false);
            if (!succeed)
            {
                throw new InvalidOperationException("delayed feature failed");
            }
            return new AgentFeature { Resource = resource };
        }
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
