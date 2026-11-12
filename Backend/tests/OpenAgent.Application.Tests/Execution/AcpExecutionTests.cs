using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using OpenAgent.Application.Conversation.Lock;
using OpenAgent.Application.Tests.Runtime;
using OpenAgent.Application.Tests.TestDoubles;
using OpenAgent.Capabilities.Security;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Runtime;
using OpenAgent.Contracts.Security;
using Xunit;

namespace OpenAgent.Application.Tests.Execution;

/// <summary>测试生产默认执行器，不使用保留在测试目录的旧 MAF 循环。</summary>
public sealed class AcpExecutionTests
{
    private static readonly AgentUserContext User = new() { TenantId = "tenant-1", UserId = "user-1" };
    private static AgentRequest Request => new()
    {
        AgentId = "test-agent",
        LlmProfileId = "test-model",
        ConversationId = "acp-conversation",
        TraceId = "acp-trace",
        Query = "继续处理"
    };

    [Fact]
    public async Task DefaultExecution_UsesDriverAndPlatformTool_PersistsBeforeTerminalEventAndReplaysHistory()
    {
        RecordingDriver driver = new(commit: true);
        await using AgentExecutorUsageTests.TestRuntime runtime = AgentExecutorUsageTests.CreateRuntime(driver);
        for (int turn = 0; turn < 2; turn++)
        {
            await foreach (AgentStreamEvent item in runtime.Executor.ExecuteStreamingAsync(Request, User, CancellationToken.None))
            {
                if (item.Type == AgentStreamEventType.Usage)
                {
                    ConversationRecord record = (await runtime.Store.GetRecordAsync("tenant-1", Request.ConversationId!))!;
                    Assert.Equal(ConversationStatus.Completed, record.Status);
                    Assert.Equal(turn + 1, record.Messages.Count(message => message.Role == "user"));
                }
            }
        }
        // 第二轮驱动调用证明会话复用与身份传递。
        Assert.Equal(2, driver.Requests.Count);
        Assert.Equal(driver.Committed[0], driver.Requests[1].HistoryCheckpoint);
        Assert.Equal(2, driver.Committed.Count);
        RuntimeInvocation second = driver.Requests[1];
        Assert.Equal("tenant-1", second.Turn.TenantId);
        Assert.Equal("user-1", second.Turn.UserId);
        Assert.Equal("acp-trace", second.Turn.TraceId);
        Assert.Contains(second.Messages, message => message.Role == "assistant" && message.Content.Contains("ACP 回答", StringComparison.Ordinal));
        Assert.Contains(second.Messages, message => message.Role == "tool" && message.Content.Contains("tenant-1", StringComparison.Ordinal));
        ConversationRecord final = (await runtime.Store.GetRecordAsync("tenant-1", Request.ConversationId!))!;
        Assert.DoesNotContain(final.Messages, message => message.TokenUsage is not null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedDriver_PersistsPartialTextAndCorrectStatus(bool cancelled)
    {
        RecordingDriver driver = new(cancelled ? new OperationCanceledException("cancelled") : new InvalidOperationException("driver failed"));
        await using AgentExecutorUsageTests.TestRuntime runtime = AgentExecutorUsageTests.CreateRuntime(driver);
        await Assert.ThrowsAnyAsync<Exception>(() => runtime.Executor.ExecuteAsync(Request, User, CancellationToken.None));
        ConversationRecord record = (await runtime.Store.GetRecordAsync("tenant-1", Request.ConversationId!))!;
        Assert.Equal(cancelled ? ConversationStatus.Cancelled : ConversationStatus.Failed, record.Status);
        Assert.Contains(record.Messages, message => message.Role == "assistant" && message.Content.Contains("ACP 回答", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(ConversationStatus.Running)]
    [InlineData(ConversationStatus.Completed)]
    [InlineData(ConversationStatus.Failed)]
    [InlineData(ConversationStatus.Cancelled)]
    public async Task ExecuteAsync_OtherUserConversation_RejectsWithoutPersistingFailure(ConversationStatus status)
    {
        RecordingDriver driver = new();
        await using AgentExecutorUsageTests.TestRuntime runtime = AgentExecutorUsageTests.CreateRuntime(driver);
        Assert.True(await runtime.Store.CreateAsync(new ConversationRecord
        {
            TenantId = "tenant-1",
            UserId = "user-1",
            ConversationId = Request.ConversationId!,
            Status = status,
            Version = 7,
            MessageCount = 1,
            Messages = [new() { MessageId = "original", Sequence = 1, Role = "user", Content = "owner message" }]
        }));
        AgentUserContext otherUser = new() { TenantId = "tenant-1", UserId = "user-2" };

        AgentException error = await Assert.ThrowsAsync<AgentException>(() =>
            runtime.Executor.ExecuteAsync(Request, otherUser, CancellationToken.None));

        Assert.Equal(AgentErrorCode.PermissionDenied, error.ErrorCode);
        Assert.Empty(driver.Requests);
        ConversationRecord record = Assert.IsType<ConversationRecord>(
            await runtime.Store.GetRecordAsync("tenant-1", Request.ConversationId!));
        Assert.Equal("user-1", record.UserId);
        Assert.Equal(status, record.Status);
        Assert.Equal(7, record.Version);
        Assert.Equal(1, record.MessageCount);
        Assert.Equal("owner message", Assert.Single(record.Messages).Content);
    }

    [Theory]
    [InlineData(ConversationStatus.Running)]
    [InlineData(ConversationStatus.Failed)]
    public async Task ExecuteAsync_LockAlreadyHeld_RejectsWithoutWritingOrReleasingOtherLock(ConversationStatus status)
    {
        InMemoryConversationLock conversationLock = new();
        RecordingDriver driver = new();
        await using AgentExecutorUsageTests.TestRuntime runtime = AgentExecutorUsageTests.CreateRuntime(driver,
            configure: services => services.AddSingleton<IConversationLock>(conversationLock));
        Assert.True(await runtime.Store.CreateAsync(new ConversationRecord
        {
            TenantId = "tenant-1", UserId = "user-1", ConversationId = Request.ConversationId!,
            Status = status, Version = 7
        }));
        await using IConversationLockHandle? handle = await conversationLock.TryAcquireAsync(
            "tenant-1", Request.ConversationId!, TimeSpan.FromSeconds(30));
        Assert.NotNull(handle);

        AgentException error = await Assert.ThrowsAsync<AgentException>(() =>
            runtime.Executor.ExecuteAsync(Request, User, CancellationToken.None));

        Assert.Equal(AgentErrorCode.Conflict, error.ErrorCode);
        Assert.Empty(driver.Requests);
        ConversationRecord record = Assert.IsType<ConversationRecord>(
            await runtime.Store.GetRecordAsync("tenant-1", Request.ConversationId!));
        Assert.Equal(status, record.Status);
        Assert.Equal(7, record.Version);
        Assert.Empty(record.Messages);
        Assert.True(handle.IsHeld);
        Assert.Null(await conversationLock.TryAcquireAsync("tenant-1", Request.ConversationId!, TimeSpan.FromSeconds(30)));
    }

    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("cancelled")]
    public async Task ExecuteAsync_LockLostBeforeFinalization_DoesNotPersist(string outcome)
    {
        bool held = true;
        Mock<IConversationLockHandle> handle = new();
        handle.SetupGet(value => value.IsHeld).Returns(() => held);
        handle.SetupGet(value => value.TenantId).Returns("tenant-1");
        handle.SetupGet(value => value.ConversationId).Returns(Request.ConversationId!);
        Mock<IConversationLock> conversationLock = new();
        conversationLock.Setup(value => value.TryAcquireAsync("tenant-1", Request.ConversationId!,
                It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        Exception? failure = outcome switch
        {
            "failure" => new InvalidOperationException("runtime failed"),
            "cancelled" => new OperationCanceledException("runtime cancelled"),
            _ => null
        };
        RecordingDriver driver = new(failure)
        {
            OnDispose = () => { held = false; return Task.CompletedTask; }
        };
        await using AgentExecutorUsageTests.TestRuntime runtime = AgentExecutorUsageTests.CreateRuntime(driver,
            configure: services => services.AddSingleton(conversationLock.Object));
        Assert.True(await runtime.Store.CreateAsync(new ConversationRecord
        {
            TenantId = "tenant-1", UserId = "user-1", ConversationId = Request.ConversationId!,
            Status = ConversationStatus.Running, Version = 7
        }));

        Exception error = await Assert.ThrowsAnyAsync<Exception>(() =>
            runtime.Executor.ExecuteAsync(Request, User, CancellationToken.None));

        if (failure is null)
        {
            Assert.Equal(AgentErrorCode.Conflict, Assert.IsType<AgentException>(error).ErrorCode);
        }
        else
        {
            Assert.Same(failure, error);
        }
        ConversationRecord record = Assert.IsType<ConversationRecord>(
            await runtime.Store.GetRecordAsync("tenant-1", Request.ConversationId!));
        Assert.Equal(ConversationStatus.Running, record.Status);
        Assert.Equal(7, record.Version);
        Assert.Empty(record.Messages);
    }

    [Fact]
    public async Task ToolPermissionRevokedAfterDiscovery_CallbackRejectsInvocation()
    {
        RecordingDriver driver = new();
        await using AgentExecutorUsageTests.TestRuntime runtime = AgentExecutorUsageTests.CreateRuntime(driver,
            configure: services =>
            {
                services.RemoveAll<IAgentAuthorizationService>();
                services.AddSingleton<IAgentAuthorizationService, DenyInvocation>();
            });
        AgentException error = await Assert.ThrowsAsync<AgentException>(() => runtime.Executor.ExecuteAsync(Request, User, CancellationToken.None));
        Assert.Equal(AgentErrorCode.PermissionDenied, error.ErrorCode);
    }

    [Fact]
    public async Task ConsumerStops_PersistsCancelledPartialResponse()
    {
        RecordingDriver driver = new();
        await using AgentExecutorUsageTests.TestRuntime runtime = AgentExecutorUsageTests.CreateRuntime(driver);
        driver.OnDispose = async () =>
        {
            ConversationRecord active = (await runtime.Store.GetRecordAsync("tenant-1", Request.ConversationId!))!;
            Assert.NotEqual(ConversationStatus.Cancelled, active.Status);
        };
        await using (IAsyncEnumerator<AgentStreamEvent> stream = runtime.Executor.ExecuteStreamingAsync(Request, User, CancellationToken.None).GetAsyncEnumerator())
        {
            Assert.True(await stream.MoveNextAsync());
        }
        ConversationRecord record = (await runtime.Store.GetRecordAsync("tenant-1", Request.ConversationId!))!;
        Assert.Equal(ConversationStatus.Cancelled, record.Status);
    }

    [Fact]
    public async Task CleanupFailsAfterCommit_DoesNotOverwritePersistedCompletion()
    {
        RecordingDriver driver = new(commit: true)
        {
            OnDispose = () => Task.FromException(new InvalidOperationException("Cleanup failed"))
        };
        await using AgentExecutorUsageTests.TestRuntime runtime = AgentExecutorUsageTests.CreateRuntime(driver);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.Executor.ExecuteAsync(Request, User, CancellationToken.None));
        ConversationRecord record = (await runtime.Store.GetRecordAsync("tenant-1", Request.ConversationId!))!;
        Assert.Equal(ConversationStatus.Completed, record.Status);
        Assert.Single(record.Messages, message => message.Role == "assistant" && message.Content == "ACP 回答");
    }

    private sealed class RecordingDriver(Exception? failure = null, bool commit = false) : IAgentRuntimeDriver
    {
        internal List<RuntimeInvocation> Requests { get; } = [];
        internal List<string?> Committed { get; } = [];
        internal Func<Task>? OnDispose { get; set; }

        public async IAsyncEnumerable<AgentStreamEvent> RunAsync(RuntimeInvocation request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            try
            {
                Requests.Add(request);
                yield return new() { Type = AgentStreamEventType.Content, Content = "ACP 回答" };
                if (failure is not null)
                {
                    throw failure;
                }
                RuntimeTool tool = Assert.Single(request.Tools, tool => tool.Name == "get_current_user_profile");
                yield return new() { Type = AgentStreamEventType.ToolCall, ToolCallId = "profile-1", ToolName = tool.Name };
                string result = await tool.InvokeAsync(new Dictionary<string, object?>(), cancellationToken);
                yield return new() { Type = AgentStreamEventType.ToolResult, ToolCallId = "profile-1", ToolName = tool.Name, Content = result };
                yield return new() { Type = AgentStreamEventType.Usage, ModelId = request.Profile.Model.ModelId, TraceId = request.Turn.TraceId };
                if (commit)
                {
                    Assert.NotNull(request.CommitAsync);
                    Committed.Add(await request.CommitAsync(cancellationToken));
                }
            }
            finally
            {
                if (OnDispose is not null)
                {
                    await OnDispose();
                }
            }
        }
    }

    private sealed class DenyInvocation : IAgentAuthorizationService
    {
        public Task<bool> IsAuthorizedAsync(AgentAuthorizationRequest request, IAgentUserContext userContext,
            CancellationToken cancellationToken = default) => Task.FromResult(request.Action != "invoke");
    }
}
