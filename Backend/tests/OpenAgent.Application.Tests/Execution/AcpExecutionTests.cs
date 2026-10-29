using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
        RecordingDriver driver = new();
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
            do
            {
                Assert.True(await stream.MoveNextAsync());
            } while (stream.Current.Type == AgentStreamEventType.ExecutionStatus);
        }
        ConversationRecord record = (await runtime.Store.GetRecordAsync("tenant-1", Request.ConversationId!))!;
        Assert.Equal(ConversationStatus.Cancelled, record.Status);
    }

    private sealed class RecordingDriver(Exception? failure = null) : IAgentRuntimeDriver
    {
        internal List<RuntimeInvocation> Requests { get; } = [];
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
