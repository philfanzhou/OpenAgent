using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using OpenAgent.Application.Execution;
using OpenAgent.Application.Execution.Admission;
using OpenAgent.Application.Tests.Runtime;
using OpenAgent.Application.Tests.TestDoubles;
using OpenAgent.Application.Tooling.Abstractions;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Runtime;
using OpenAgent.Contracts.Security;
using Xunit;

namespace OpenAgent.Application.Tests.Execution.Admission;

public sealed class RuntimeAdmissionTests
{
    private static readonly AgentUserContext User = new() { UserId = "user-1", TenantId = "tenant-1" };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteStreamingAsync_QueuedCancelOrTimeout_DoesNotPrepareOrInvoke(bool timeout)
    {
        AdmissionOptions limits = Limits();
        limits.QueueTimeoutSeconds = 1;
        TurnAdmission admission = new(Options.Create(limits));
        using AdmissionTicket occupied = admission.Enter(User.TenantId!, default);
        ScriptedRuntimeDriver driver = new([]);
        Mock<IAgentFeatureFactory> factory = new(MockBehavior.Strict);
        Mock<IConversationLock> conversationLock = new(MockBehavior.Strict);
        await using AgentExecutorUsageTests.TestRuntime runtime = AgentExecutorUsageTests.CreateRuntime(driver, configure: services =>
        {
            services.AddSingleton(admission);
            services.AddSingleton(factory.Object);
            services.AddSingleton(conversationLock.Object);
        });
        using CancellationTokenSource cancel = new();
        await using IAsyncEnumerator<AgentStreamEvent> events = runtime.Executor.ExecuteStreamingAsync(Request("queued"), User, cancel.Token).GetAsyncEnumerator();
        Assert.True(await events.MoveNextAsync());
        Assert.Equal("queued", events.Current.Status);
        if (timeout)
        {
            RuntimeAdmissionException error = await Assert.ThrowsAsync<RuntimeAdmissionException>(() => events.MoveNextAsync().AsTask());
            Assert.Equal(AgentErrorCode.RuntimeQueueTimeout, error.ErrorCode);
        }
        else
        {
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => events.MoveNextAsync().AsTask());
        }
        Assert.Empty(driver.Invocations);
        factory.VerifyNoOtherCalls();
        conversationLock.VerifyNoOtherCalls();
        Assert.Null(await runtime.Store.GetRecordAsync(User.TenantId!, "queued"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteStreamingAsync_ExecutionCancelOrTimeout_ReleasesFeaturesAndConversationLock(bool timeout)
    {
        Mock<IAsyncDisposable> resource = new();
        Mock<IAgentFeatureFactory> factory = new();
        factory.Setup(item => item.CreateAsync(It.IsAny<string>(), It.IsAny<AgentConfig>(), It.IsAny<IAgentUserContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new AgentFeature { Resource = resource.Object });
        ScriptedRuntimeDriver driver = new([new AgentStreamEvent { Type = AgentStreamEventType.Content, Content = "partial" }], holdUntilCancelled: true);
        await using AgentExecutorUsageTests.TestRuntime runtime = AgentExecutorUsageTests.CreateRuntime(driver, configure: services =>
        {
            services.Configure<AdmissionOptions>(options => { options.MaxConcurrentTurns = 1; options.MaxConcurrentPerTenant = 1; options.ExecutionTimeoutSeconds = timeout ? 1 : 30; });
            services.AddSingleton(factory.Object);
        });
        using CancellationTokenSource cancel = new();
        await using IAsyncEnumerator<AgentStreamEvent> events = runtime.Executor.ExecuteStreamingAsync(Request("running"), User, cancel.Token).GetAsyncEnumerator();
        Assert.True(await events.MoveNextAsync());
        Assert.Equal("started", events.Current.Status);
        Assert.True(await events.MoveNextAsync());
        Assert.Equal("partial", events.Current.Content);
        if (timeout)
        {
            AgentException error = await Assert.ThrowsAsync<AgentException>(() => events.MoveNextAsync().AsTask());
            Assert.Equal(AgentErrorCode.RuntimeExecutionTimeout, error.ErrorCode);
        }
        else
        {
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => events.MoveNextAsync().AsTask());
        }
        resource.Verify(item => item.DisposeAsync(), Times.Once);
        ConversationRecord record = Assert.IsType<ConversationRecord>(await runtime.Store.GetRecordAsync(User.TenantId!, "running"));
        Assert.Equal(timeout ? ConversationStatus.Failed : ConversationStatus.Cancelled, record.Status);
        // Same conversation must be lockable immediately after cleanup, and admission fully recovered.
        await using AsyncServiceScope retryScope = runtime.CreateScope();
        await using IAsyncEnumerator<AgentStreamEvent> retry = retryScope.ServiceProvider.GetRequiredService<RuntimeAgentExecutor>().ExecuteStreamingAsync(Request("running"), User, default).GetAsyncEnumerator();
        Assert.True(await retry.MoveNextAsync());
        Assert.Equal("started", retry.Current.Status);
        Assert.True(await retry.MoveNextAsync());
        Assert.Equal("partial", retry.Current.Content);
    }

    [Fact]
    public async Task ExecuteStreamingAsync_AuthorizationFails_DoesNotConsumeAdmission()
    {
        Mock<IAgentRuntimeResolver> resolver = new();
        resolver.Setup(item => item.ResolveAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IAgentUserContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AgentException(AgentErrorCode.PermissionDenied));
        TurnAdmission admission = new(Options.Create(Limits()));
        using AdmissionTicket occupied = admission.Enter(User.TenantId!, default);
        ScriptedRuntimeDriver driver = new([]);
        await using AgentExecutorUsageTests.TestRuntime runtime = AgentExecutorUsageTests.CreateRuntime(driver, configure: services =>
        {
            services.AddSingleton(resolver.Object);
            services.AddSingleton(admission);
        });
        AgentException error = await Assert.ThrowsAsync<AgentException>(() => runtime.Executor.ExecuteAsync(Request("denied"), User, default));
        Assert.Equal(AgentErrorCode.PermissionDenied, error.ErrorCode);
        Assert.Empty(driver.Invocations);
    }

    private static AdmissionOptions Limits() => new() { MaxConcurrentTurns = 1, MaxConcurrentPerTenant = 1 };
    private static AgentRequest Request(string conversation) => new()
    {
        AgentId = "test-agent", LlmProfileId = "test-model", ConversationId = conversation, Query = "hello"
    };
}
