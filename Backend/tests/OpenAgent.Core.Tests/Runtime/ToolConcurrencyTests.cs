using System.Diagnostics;
using Microsoft.Extensions.AI;
using OpenAgent.Contracts.Capabilities;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Capabilities;
using OpenAgent.Core.Runtime.Agent;
using OpenAgent.Core.Tests.TestDoubles;
using Xunit;

namespace OpenAgent.Core.Tests.Runtime;

/// <summary>
/// 并发类别语义验证：ReadOnly 工具可重叠执行；Exclusive 工具经共享信号量
/// 串行；排队等锁计入单次调用超时并给出可区分的错误文案。
/// </summary>
public class ToolConcurrencyTests
{
    [Fact]
    public async Task ExclusiveTools_SharedGate_NeverOverlap()
    {
        var gate = new SemaphoreSlim(1, 1);
        var tracker = new OverlapTracker();
        AITool first = IsolatedToolFunction.Wrap(
            new RecordingTool("write_a", tracker, TimeSpan.FromMilliseconds(120)),
            concurrency: ToolConcurrency.Exclusive,
            exclusiveGate: gate);
        AITool second = IsolatedToolFunction.Wrap(
            new RecordingTool("write_b", tracker, TimeSpan.FromMilliseconds(120)),
            concurrency: ToolConcurrency.Exclusive,
            exclusiveGate: gate);

        await Task.WhenAll(
            InvokeAsync(first),
            InvokeAsync(second));

        Assert.False(tracker.Overlapped, "exclusive tool calls must be serialized by the shared gate");
        Assert.Equal(2, tracker.Calls.Count);
    }

    [Fact]
    public async Task ReadOnlyTools_WithoutGate_MayOverlap()
    {
        var tracker = new OverlapTracker();
        AITool first = IsolatedToolFunction.Wrap(
            new RecordingTool("read_a", tracker, TimeSpan.FromMilliseconds(150)),
            concurrency: ToolConcurrency.ReadOnly);
        AITool second = IsolatedToolFunction.Wrap(
            new RecordingTool("read_b", tracker, TimeSpan.FromMilliseconds(150)),
            concurrency: ToolConcurrency.ReadOnly);

        var stopwatch = Stopwatch.StartNew();
        await Task.WhenAll(
            InvokeAsync(first),
            InvokeAsync(second));
        stopwatch.Stop();

        // 重叠执行时总耗时明显小于两次串行延迟之和（留足 CI 抖动余量）。
        Assert.True(
            tracker.Overlapped,
            $"read-only calls should overlap (took {stopwatch.ElapsedMilliseconds}ms)");
    }

    [Fact]
    public async Task QueuedExclusiveTool_TimesOutWithDistinctMessage()
    {
        // 第一个调用持有独占锁 500ms；第二个调用 100ms 超时，且大部分时间在排队。
        var gate = new SemaphoreSlim(1, 1);
        var tracker = new OverlapTracker();
        AITool slow = IsolatedToolFunction.Wrap(
            new RecordingTool("write_slow", tracker, TimeSpan.FromMilliseconds(500)),
            concurrency: ToolConcurrency.Exclusive,
            exclusiveGate: gate);
        AITool queued = IsolatedToolFunction.Wrap(
            new RecordingTool("write_queued", tracker, TimeSpan.FromMilliseconds(500)),
            toolCallTimeout: TimeSpan.FromMilliseconds(100),
            concurrency: ToolConcurrency.Exclusive,
            exclusiveGate: gate);

        Task slowTask = InvokeAsync(slow);
        Task<string> queuedTask = InvokeAsync(queued);
        string result = await queuedTask;
        await slowTask;

        Assert.Contains(
            "waiting for another exclusive tool call",
            result,
            StringComparison.Ordinal);
        Assert.Contains("\"timedOut\":true", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_UndeclaredTool_FallsBackToExclusive()
    {
        var stub = new PlainTool("mcp__srv__tool");
        Assert.Equal(ToolConcurrency.Exclusive, ToolConcurrencyRules.Resolve(stub));
    }

    [Fact]
    public async Task ExecuteStreamingAsync_TwoReadOnlyCallsInOneUpdate_ExecuteConcurrently()
    {
        // 走真实 AgentFactory/FICC 路径：同一条 assistant 消息里的两个 ReadOnly
        // 工具调用必须重叠执行（AllowConcurrentInvocation=true 只放行白名单）。
        var provider = new SequenceChatProvider(
        [
            [new ChatResponseUpdate(Microsoft.Extensions.AI.ChatRole.Assistant,
                [
                    new FunctionCallContent("call-1", "probe_read_a"),
                    new FunctionCallContent("call-2", "probe_read_b")
                ])],
            [new ChatResponseUpdate(Microsoft.Extensions.AI.ChatRole.Assistant, "两个读取都完成了")]
        ]);
        var source = new ProbingCapabilitySource();
        await using AgentExecutorUsageTests.TestRuntime runtime =
            AgentExecutorUsageTests.CreateRuntime(provider, source, maxTurns: 4);

        List<AgentStreamEvent> events = [];
        await foreach (AgentStreamEvent streamEvent in runtime.Executor.ExecuteStreamingAsync(
            new AgentRequest
            {
                Query = "hello",
                AgentId = "test-agent",
                LlmProfileId = "test-model",
                ConversationId = "parallel-read-conversation",
                TraceId = "trace-parallel-read"
            },
            new AgentUserContext { UserId = "user-1", TenantId = "tenant-1" },
            CancellationToken.None))
        {
            events.Add(streamEvent);
        }

        Assert.Equal(2, events.Count(item => item.Type == AgentStreamEventType.ToolResult));
        Assert.True(source.Tracker.Overlapped, "read-only tool calls must execute concurrently");
    }

    /// <summary>暴露两个 ReadOnly 探针工具，记录执行区间供重叠断言。</summary>
    private sealed class ProbingCapabilitySource : ICapabilitySource
    {
        public OverlapTracker Tracker { get; } = new();

        public Task<IReadOnlyList<CapabilityDefinition>> DiscoverAsync(
            string agentId,
            AgentConfig config,
            IAgentUserContext user,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<CapabilityDefinition> definitions =
            [
                new CapabilityDefinition(
                    "probe_read_a",
                    "Simulates a read-only tool.",
                    "{\"type\":\"object\"}",
                    AgentResourceType.Tool,
                    "test/probe_read_a",
                    async (_, _) =>
                    {
                        await Tracker.RunAsync("probe_read_a", TimeSpan.FromMilliseconds(200));
                        return "a";
                    },
                    Concurrency: ToolConcurrency.ReadOnly),
                new CapabilityDefinition(
                    "probe_read_b",
                    "Simulates a read-only tool.",
                    "{\"type\":\"object\"}",
                    AgentResourceType.Tool,
                    "test/probe_read_b",
                    async (_, _) =>
                    {
                        await Tracker.RunAsync("probe_read_b", TimeSpan.FromMilliseconds(200));
                        return "b";
                    },
                    Concurrency: ToolConcurrency.ReadOnly)
            ];
            return Task.FromResult(definitions);
        }
    }

    private static async Task<string> InvokeAsync(AITool wrapped) =>
        Assert.IsType<string>(await Assert.IsAssignableFrom<AIFunction>(wrapped)
            .InvokeAsync(new AIFunctionArguments(), CancellationToken.None));

    /// <summary>记录进入/退出时刻，判定两次调用区间是否重叠。</summary>
    private sealed class OverlapTracker
    {
        private readonly object _lock = new();
        private readonly List<(string Name, long Entered, long Exited)> _calls = [];

        public bool Overlapped
        {
            get
            {
                lock (_lock)
                {
                    for (int i = 0; i < _calls.Count; i++)
                    {
                        for (int j = i + 1; j < _calls.Count; j++)
                        {
                            (string _, long enterA, long exitA) = _calls[i];
                            (string _, long enterB, long exitB) = _calls[j];
                            if (enterA < exitB && enterB < exitA)
                            {
                                return true;
                            }
                        }
                    }
                    return false;
                }
            }
        }

        public IReadOnlyList<(string Name, long Entered, long Exited)> Calls
        {
            get
            {
                lock (_lock)
                {
                    return _calls.ToArray();
                }
            }
        }

        public async Task RunAsync(string name, TimeSpan delay)
        {
            long entered = Stopwatch.GetTimestamp();
            try
            {
                await Task.Delay(delay);
            }
            finally
            {
                long exited = Stopwatch.GetTimestamp();
                lock (_lock)
                {
                    _calls.Add((name, entered, exited));
                }
            }
        }
    }

    private sealed class RecordingTool(string name, OverlapTracker tracker, TimeSpan delay) : AIFunction
    {
        public override string Name => name;
        public override string Description => "records execution interval";
        public override System.Text.Json.JsonElement JsonSchema =>
            System.Text.Json.JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone();

        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            await tracker.RunAsync(name, delay);
            return $"done:{name}";
        }
    }

    private sealed class PlainTool(string name) : AIFunction
    {
        public override string Name => name;
        public override string Description => "plain";
        public override System.Text.Json.JsonElement JsonSchema =>
            System.Text.Json.JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone();

        protected override ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<object?>("ok");
    }
}
