using System.Runtime.CompilerServices;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Runtime;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Application.Tests.TestDoubles;

/// <summary>
/// 按脚本回放事件的运行时驱动替身：替代已移除的 MAF 回归夹具。
/// holdUntilCancelled 用于模拟长轮次被客户端取消；failure 在事件回放完后抛出。
/// </summary>
internal sealed class ScriptedRuntimeDriver(
    IReadOnlyList<AgentStreamEvent> events,
    Exception? failure = null,
    bool holdUntilCancelled = false) : IAgentRuntimeDriver
{
    internal List<RuntimeInvocation> Invocations { get; } = [];

    public async IAsyncEnumerable<AgentStreamEvent> RunAsync(
        RuntimeInvocation invocation,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Invocations.Add(invocation);
        foreach (AgentStreamEvent item in events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return item;
        }
        if (holdUntilCancelled)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
        if (failure is not null)
        {
            throw failure;
        }
    }
}

/// <summary>
/// 按脚本调用本轮工具目录的驱动替身：对每个 (工具名, 参数) 依次发出 tool_call、
/// 真实调用平台 RuntimeTool、发出 tool_result，最后给出正文事件。用于在无模型
/// 循环的前提下覆盖工具链路（MCP 客户端、隔离包装、授权）的端到端行为。
/// </summary>
internal sealed class ToolInvokingDriver(
    IReadOnlyList<(string ToolName, IReadOnlyDictionary<string, object?> Arguments)> calls,
    string completion = "done",
    bool invokeInParallel = false) : IAgentRuntimeDriver
{
    internal List<RuntimeInvocation> Invocations { get; } = [];

    public async IAsyncEnumerable<AgentStreamEvent> RunAsync(
        RuntimeInvocation invocation,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Invocations.Add(invocation);
        RuntimeTool Resolve(string toolName) =>
            invocation.Tools.FirstOrDefault(item => item.Name == toolName)
                ?? throw new InvalidOperationException($"Driver script references unknown tool '{toolName}'.");
        if (invokeInParallel)
        {
            // 先全部播报调用，再并发执行并按完成序播报结果——模拟 harness 的并行工具轮。
            List<(string Id, string Name, Task<string> Task)> pending = [];
            for (int index = 0; index < calls.Count; index++)
            {
                (string toolName, IReadOnlyDictionary<string, object?> arguments) = calls[index];
                pending.Add(($"driver-call-{index}", toolName, Resolve(toolName).InvokeAsync(arguments, cancellationToken)));
                yield return new AgentStreamEvent
                {
                    Type = AgentStreamEventType.ToolCall,
                    ToolCallId = pending[^1].Id,
                    ToolName = toolName,
                    ToolArguments = arguments
                };
            }
            foreach ((string id, string name, Task<string> task) in pending)
            {
                yield return new AgentStreamEvent
                {
                    Type = AgentStreamEventType.ToolResult,
                    ToolCallId = id,
                    ToolName = name,
                    Content = await task.ConfigureAwait(false)
                };
            }
        }
        else
        {
            for (int index = 0; index < calls.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                (string toolName, IReadOnlyDictionary<string, object?> arguments) = calls[index];
                string callId = $"driver-call-{index}";
                yield return new AgentStreamEvent
                {
                    Type = AgentStreamEventType.ToolCall,
                    ToolCallId = callId,
                    ToolName = toolName,
                    ToolArguments = arguments
                };
                string result = await Resolve(toolName).InvokeAsync(arguments, cancellationToken).ConfigureAwait(false);
                yield return new AgentStreamEvent
                {
                    Type = AgentStreamEventType.ToolResult,
                    ToolCallId = callId,
                    ToolName = toolName,
                    Content = result
                };
            }
        }
        yield return new AgentStreamEvent { Type = AgentStreamEventType.Content, Content = completion };
    }
}
