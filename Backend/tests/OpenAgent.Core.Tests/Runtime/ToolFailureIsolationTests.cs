using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using OpenAgent.Contracts.Capabilities;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Capabilities;
using OpenAgent.Core.Runtime.Agent;
using OpenAgent.Core.Tests.TestDoubles;
using Xunit;

namespace OpenAgent.Core.Tests.Runtime;

public class ToolFailureIsolationTests
{
    [Fact]
    public async Task ExecuteStreamingAsync_ToolThrowsRepeatedly_RunCompletesWithErrorResults()
    {
        // 工具/MCP 连续失败时（旧实现 3 次后 FunctionInvokingChatClient 重抛异常终止整轮执行），
        // 异常必须被隔离成错误结果回传给模型，模型据此调整并给出最终回复。
        var provider = new SequenceChatProvider(
        [
            [new ChatResponseUpdate(ChatRole.Assistant,
                [new FunctionCallContent("call-1", "always_failing_tool")])],
            [new ChatResponseUpdate(ChatRole.Assistant,
                [new FunctionCallContent("call-2", "always_failing_tool")])],
            [new ChatResponseUpdate(ChatRole.Assistant,
                [new FunctionCallContent("call-3", "always_failing_tool")])],
            [new ChatResponseUpdate(ChatRole.Assistant, "工具持续失败，我改为直接回答")]
        ]);
        await using AgentExecutorUsageTests.TestRuntime runtime =
            AgentExecutorUsageTests.CreateRuntime(provider, new ThrowingCapabilitySource(), maxTurns: 8);

        List<AgentStreamEvent> events = [];
        await foreach (AgentStreamEvent streamEvent in runtime.Executor.ExecuteStreamingAsync(
            CreateRequest("tool-failure-conversation"),
            User,
            CancellationToken.None))
        {
            events.Add(streamEvent);
        }

        Assert.Equal(4, provider.Requests.Count);
        List<AgentStreamEvent> results = events
            .Where(item => item.Type == AgentStreamEventType.ToolResult)
            .ToList();
        Assert.Equal(3, results.Count);
        Assert.All(results, result => Assert.Contains("'always_failing_tool' failed", result.Content));
        // 后续请求必须带回错误结果，模型才能看到失败原因并继续。
        Assert.Contains(
            provider.Requests[1].SelectMany(message => message.Contents).OfType<FunctionResultContent>(),
            result => result.CallId == "call-1");
        Assert.Contains(events, item => item.Type == AgentStreamEventType.Content
            && item.Content == "工具持续失败，我改为直接回答");
    }

    [Fact]
    public async Task ExecuteStreamingAsync_UnknownToolCall_ContinuesInsteadOfTerminating()
    {
        // 未注册的工具名（如本次运行中某 MCP server 连接失败、工具未注册，或模型幻觉出名字）
        // 不能终止循环，必须把 tool-not-found 结果回传给模型让它自行调整。
        var provider = new SequenceChatProvider(
        [
            [new ChatResponseUpdate(ChatRole.Assistant,
                [new FunctionCallContent("call-1", "mcp__down_server__tool")])],
            [new ChatResponseUpdate(ChatRole.Assistant, "该工具当前不可用，已改为直接回答")]
        ]);
        await using AgentExecutorUsageTests.TestRuntime runtime =
            AgentExecutorUsageTests.CreateRuntime(provider, maxTurns: 4);

        List<AgentStreamEvent> events = [];
        await foreach (AgentStreamEvent streamEvent in runtime.Executor.ExecuteStreamingAsync(
            CreateRequest("unknown-tool-conversation"),
            User,
            CancellationToken.None))
        {
            events.Add(streamEvent);
        }

        Assert.Equal(2, provider.Requests.Count);
        Assert.Contains(
            provider.Requests[1].SelectMany(message => message.Contents).OfType<FunctionResultContent>(),
            result => result.CallId == "call-1");
        Assert.Contains(events, item => item.Type == AgentStreamEventType.Content
            && item.Content == "该工具当前不可用，已改为直接回答");
    }

    [Fact]
    public async Task ExecuteStreamingAsync_ProviderFailsAfterToolCall_PersistsStreamedToolMessages()
    {
        // 执行失败时已流出的工具调用/结果必须随失败状态持久化，
        // 刷新后前端重建的时间线才能与实时看到的过程一致。
        var provider = new SequenceChatProvider(
        [
            [new ChatResponseUpdate(ChatRole.Assistant,
                [new FunctionCallContent("call-1", "get_current_user_profile")])],
            []
        ], new InvalidOperationException("provider failed mid-run"));
        await using AgentExecutorUsageTests.TestRuntime runtime =
            AgentExecutorUsageTests.CreateRuntime(provider);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ConsumeStreamAsync(runtime, "tool-failure-persist-conversation"));

        ConversationRecord record = Assert.IsType<ConversationRecord>(
            await runtime.Store.GetRecordAsync("tenant-1", "tool-failure-persist-conversation"));
        Assert.Equal(ConversationStatus.Failed, record.Status);
        Assert.Equal("user", record.Messages[0].Role);
        ConversationMessage callRow = Assert.Single(record.Messages,
            message => message.Role == "assistant" && message.ToolCallId == "call-1");
        Assert.Equal("get_current_user_profile", callRow.ToolName);
        Assert.NotNull(record.Messages.SingleOrDefault(message =>
            message.Role == "tool" && message.ToolCallId == "call-1"));
        ConversationMessage partial = record.Messages[^1];
        Assert.Equal("assistant", partial.Role);
        Assert.Equal("Failed", partial.Metadata?.ExecutionStatus);
    }

    [Fact]
    public async Task ExecuteStreamingAsync_EmptyToolArguments_ReturnsHintAndRunContinues()
    {
        // 模型传入空参数（"arguments":""/缺省，Arguments 为 null）时：工具返回可操作的
        // 参数缺失提示，模型看到后直接回答用户——执行不能终止，回传请求也不能被
        // "arguments":"null" 破坏。
        var provider = new SequenceChatProvider(
        [
            [new ChatResponseUpdate(ChatRole.Assistant,
                [new FunctionCallContent("call-1", "hinting_tool")])],
            [new ChatResponseUpdate(ChatRole.Assistant, "请告诉我要分享哪个文件")]
        ]);
        await using AgentExecutorUsageTests.TestRuntime runtime =
            AgentExecutorUsageTests.CreateRuntime(provider, new HintingCapabilitySource(), maxTurns: 4);

        List<AgentStreamEvent> events = [];
        await foreach (AgentStreamEvent streamEvent in runtime.Executor.ExecuteStreamingAsync(
            CreateRequest("empty-args-conversation"),
            User,
            CancellationToken.None))
        {
            events.Add(streamEvent);
        }

        Assert.Equal(2, provider.Requests.Count);
        AgentStreamEvent result = Assert.Single(events, item =>
            item.Type == AgentStreamEventType.ToolResult && item.ToolCallId == "call-1");
        Assert.Contains("required argument", result.Content);
        Assert.Contains(events, item => item.Type == AgentStreamEventType.Content
            && item.Content == "请告诉我要分享哪个文件");
    }

    private static async Task ConsumeStreamAsync(
        AgentExecutorUsageTests.TestRuntime runtime,
        string conversationId)
    {
        await foreach (AgentStreamEvent _ in runtime.Executor.ExecuteStreamingAsync(
            CreateRequest(conversationId),
            User,
            CancellationToken.None))
        {
        }
    }

    [Fact]
    public async Task Wrap_InvocationThrows_ReturnsSanitizedErrorJsonInsteadOfPropagating()
    {
        // 原始异常文本（可能含内部地址/堆栈细节）不得透传给模型：
        // 信封只带通用失败描述 + code + errorId（日志关联），异常本体只进日志。
        AITool wrapped = IsolatedToolFunction.Wrap(new StubFunction(
            "always_failing_tool",
            _ => ValueTask.FromException<object?>(new InvalidOperationException("mcp server unreachable"))));

        object? result = await Assert.IsAssignableFrom<AIFunction>(wrapped)
            .InvokeAsync(new AIFunctionArguments(), CancellationToken.None);

        string json = Assert.IsType<string>(result);
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal(
            "Tool 'always_failing_tool' failed with an unexpected error.",
            document.RootElement.GetProperty("error").GetString());
        Assert.Equal("tool_error", document.RootElement.GetProperty("code").GetString());
        Assert.NotNull(document.RootElement.GetProperty("errorId").GetString());
        Assert.DoesNotContain("mcp server unreachable", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Wrap_TransportCancellation_ReturnsTimeoutMarkerToModel()
    {
        // 传输层超时（如 MCP HttpClient.Timeout 5 分钟触发）抛 TaskCanceledException，
        // 但外层运行并未取消——必须转成带 timedOut 标记的明确超时结果，
        // 模型才能分辨"这是超时"并决定重试还是绕开，而不是看到模糊的失败。
        AITool wrapped = IsolatedToolFunction.Wrap(new StubFunction(
            "slow_mcp_tool",
            _ => ValueTask.FromException<object?>(new TaskCanceledException("A task was canceled."))));

        object? result = await Assert.IsAssignableFrom<AIFunction>(wrapped)
            .InvokeAsync(new AIFunctionArguments(), CancellationToken.None);

        string json = Assert.IsType<string>(result);
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.True(document.RootElement.GetProperty("timedOut").GetBoolean());
        Assert.Contains("timed out", document.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Wrap_ToolExceedsCallTimeout_ReturnsTimeoutErrorAndCancelsCall()
    {
        // 挂起的工具调用必须被单次调用上限主动取消，不能等到传输层兜底；
        // 模型收到 "timed out after Ns" 才能判断等多久、要不要重试。
        bool observedCancellation = false;
        AITool wrapped = IsolatedToolFunction.Wrap(
            new StubFunction(
                "slow_mcp_tool",
                token => DelayObservingCancellationAsync(
                    TimeSpan.FromSeconds(30),
                    token,
                    () => observedCancellation = true)),
            TimeSpan.FromMilliseconds(150));

        object? result = await Assert.IsAssignableFrom<AIFunction>(wrapped)
            .InvokeAsync(new AIFunctionArguments(), CancellationToken.None);

        string json = Assert.IsType<string>(result);
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.True(document.RootElement.GetProperty("timedOut").GetBoolean());
        Assert.Contains("timed out after", document.RootElement.GetProperty("error").GetString());
        Assert.True(observedCancellation, "the in-flight call must observe cancellation");
    }

    [Fact]
    public async Task Wrap_OuterRunCancelled_PropagatesCancellation()
    {
        // 运行级取消（用户中止/停机）不是工具超时，必须照常向上传播。
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        AITool wrapped = IsolatedToolFunction.Wrap(
            new StubFunction(
                "slow_mcp_tool",
                token => ValueTask.FromException<object?>(new OperationCanceledException(token))),
            TimeSpan.FromMinutes(5));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await Assert.IsAssignableFrom<AIFunction>(wrapped)
                .InvokeAsync(new AIFunctionArguments(), cancelled.Token));
    }

    [Fact]
    public async Task ExecuteStreamingAsync_ToolTimesOut_RunContinuesWithFinalAnswer()
    {
        // 会话中工具调用超过上限：调用被取消、模型收到 timedOut 结果并继续对话，
        // 会话不能被终止（旧行为：挂到传输层超时，报错文本无法分辨超时）。
        var provider = new SequenceChatProvider(
        [
            [new ChatResponseUpdate(ChatRole.Assistant,
                [new FunctionCallContent("call-1", "slow_mcp_tool")])],
            [new ChatResponseUpdate(ChatRole.Assistant, "工具超时了，我改为直接回答")]
        ]);
        await using AgentExecutorUsageTests.TestRuntime runtime = AgentExecutorUsageTests.CreateRuntime(
            provider,
            new SlowCapabilitySource(),
            maxTurns: 4,
            configure: services => services.PostConfigure<AgentExecutionOptions>(
                options => options.ToolCallTimeoutSeconds = 1));

        List<AgentStreamEvent> events = [];
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await foreach (AgentStreamEvent streamEvent in runtime.Executor.ExecuteStreamingAsync(
            CreateRequest("tool-timeout-conversation"),
            User,
            CancellationToken.None))
        {
            events.Add(streamEvent);
        }
        stopwatch.Stop();
        Assert.True(
            stopwatch.Elapsed >= TimeSpan.FromMilliseconds(900),
            $"timeout must elapse before cancellation, took {stopwatch.ElapsedMilliseconds}ms");

        Assert.Equal(2, provider.Requests.Count);
        Assert.Contains(
            provider.Requests[1].SelectMany(message => message.Contents).OfType<FunctionResultContent>(),
            result => result.CallId == "call-1");
        AgentStreamEvent toolResult = Assert.Single(events, item =>
            item.Type == AgentStreamEventType.ToolResult && item.ToolCallId == "call-1");
        Assert.Contains("timed out", toolResult.Content);
        Assert.Contains(events, item => item.Type == AgentStreamEventType.Content
            && item.Content == "工具超时了，我改为直接回答");
        ConversationRecord record = Assert.IsType<ConversationRecord>(
            await runtime.Store.GetRecordAsync("tenant-1", "tool-timeout-conversation"));
        Assert.Equal(ConversationStatus.Completed, record.Status);
    }

    [Fact]
    public async Task Wrap_InvocationSucceeds_PassesResultThrough()
    {
        AITool wrapped = IsolatedToolFunction.Wrap(new StubFunction(
            "healthy_tool",
            _ => ValueTask.FromResult<object?>("ok")));

        object? result = await Assert.IsAssignableFrom<AIFunction>(wrapped)
            .InvokeAsync(new AIFunctionArguments(), CancellationToken.None);

        Assert.Equal("ok", result);
    }

    /// <summary>提供一个每次调用都抛异常的工具，模拟 MCP server 宕机/超时等工具层异常。</summary>
    private sealed class ThrowingCapabilitySource : ICapabilitySource
    {
        public Task<IReadOnlyList<CapabilityDefinition>> DiscoverAsync(
            string agentId,
            AgentConfig config,
            IAgentUserContext user,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CapabilityDefinition>>([
                new CapabilityDefinition(
                    "always_failing_tool",
                    "Simulates an MCP tool whose backing server is down.",
                    "{\"type\":\"object\"}",
                    AgentResourceType.Tool,
                    "test/always_failing_tool",
                    (_, _) => throw new InvalidOperationException("mcp server unreachable"))
            ]);
    }

    /// <summary>必填参数缺失时返回可操作提示的工具，与文件能力的校验提示行为一致。</summary>
    private sealed class HintingCapabilitySource : ICapabilitySource
    {
        public Task<IReadOnlyList<CapabilityDefinition>> DiscoverAsync(
            string agentId,
            AgentConfig config,
            IAgentUserContext user,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CapabilityDefinition>>([
                new CapabilityDefinition(
                    "hinting_tool",
                    "Simulates a tool that validates required arguments.",
                    "{\"type\":\"object\",\"properties\":{\"fileId\":{\"type\":\"string\"}},\"required\":[\"fileId\"]}",
                    AgentResourceType.Tool,
                    "test/hinting_tool",
                    (arguments, _) => Task.FromResult<ToolResult>(
                        arguments.ContainsKey("fileId")
                            ? "{\"fileId\":\"ok\"}"
                            : ToolResult.Error(
                                "'fileId' is a required argument; provide it and retry.",
                                "invalid_arguments")))
            ]);
    }

    /// <summary>执行超过单次调用上限的工具，模拟长时间无响应的 MCP 调用。</summary>
    private sealed class SlowCapabilitySource : ICapabilitySource
    {
        public Task<IReadOnlyList<CapabilityDefinition>> DiscoverAsync(
            string agentId,
            AgentConfig config,
            IAgentUserContext user,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CapabilityDefinition>>([
                new CapabilityDefinition(
                    "slow_mcp_tool",
                    "Simulates an MCP tool that runs past the call timeout.",
                    "{\"type\":\"object\"}",
                    AgentResourceType.Tool,
                    "test/slow_mcp_tool",
                    async (_, token) =>
                    {
                        await Task.Delay(TimeSpan.FromSeconds(30), token);
                        return "late";
                    })
            ]);
    }

    /// <summary>等待指定时长并观察取消：被取消时先回调再抛出，模拟长跑工具对令牌的响应。</summary>
    private static async ValueTask<object?> DelayObservingCancellationAsync(
        TimeSpan delay,
        CancellationToken cancellationToken,
        Action cancelled)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
            return "late";
        }
        catch (OperationCanceledException)
        {
            cancelled();
            throw;
        }
    }

    private sealed class StubFunction(
        string name,
        Func<CancellationToken, ValueTask<object?>> invoke) : AIFunction
    {
        public override string Name => name;
        public override string Description => "stub";
        public override JsonElement JsonSchema =>
            JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone();

        protected override ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken) => invoke(cancellationToken);
    }

    private static readonly AgentUserContext User = new()
    {
        UserId = "user-1",
        TenantId = "tenant-1"
    };

    private static AgentRequest CreateRequest(string conversationId) => new()
    {
        Query = "hello",
        AgentId = "test-agent",
        LlmProfileId = "test-model",
        ConversationId = conversationId,
        TraceId = $"trace-{conversationId}"
    };
}
