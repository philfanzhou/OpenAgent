using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAgent.Contracts.Requests;

namespace OpenAgent.Core.Runtime.Agent;

/// <summary>Projects one agent run's updates into platform events and partial history.</summary>
internal sealed class StreamEventAdapter(AgentExecutionScope scope, string modelId)
{
    private readonly HashSet<string> _announcedToolCalls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _toolCallNames = new(StringComparer.Ordinal);
    private readonly List<string> _announcedCallOrder = [];
    private readonly HashSet<string> _matchedCallIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _announcedArgumentCounts = new(StringComparer.Ordinal);
    private int _syntheticCallCounter;

    internal TokenUsage? Usage { get; private set; }
    internal string ModelId { get; private set; } = modelId;

    internal IEnumerable<AgentStreamEvent> Adapt(AgentResponseUpdate update)
    {
        IList<AIContent> contents = update.Contents ?? [];
        foreach (FunctionCallContent call in contents.OfType<FunctionCallContent>())
        {
            if (call.Exception != null || string.IsNullOrWhiteSpace(call.Name))
            {
                continue;
            }

            // 部分提供方不下发调用编号：合成稳定 id，前端与持久层才能把参数与
            // 结果合到同一行，而不是退化成只显示响应的“工具”占位活动。
            string callId = string.IsNullOrWhiteSpace(call.CallId)
                ? $"stream_{_syntheticCallCounter++}_{call.Name}"
                : call.CallId;
            // 部分 LLM 返回的工具调用 arguments 为 null 而非空对象：统一规格化为
            // 空字典，播报与持久化都不再出现 null 参数形态。
            IDictionary<string, object?> arguments = call.Arguments ?? new Dictionary<string, object?>();
            if (_announcedToolCalls.Add(callId))
            {
                _toolCallNames[callId] = call.Name;
                _announcedCallOrder.Add(callId);
                _announcedArgumentCounts[callId] = arguments.Count;
                scope.AppendToolCall(call.Name, callId, arguments);
                yield return new AgentStreamEvent
                {
                    Type = AgentStreamEventType.ToolCall,
                    ToolName = call.Name,
                    ToolCallId = callId,
                    ToolArguments = arguments
                };
            }
            else if (arguments is { Count: > 0 }
                && _announcedArgumentCounts.GetValueOrDefault(callId) is not > 0)
            {
                // 同一调用的后续更新补全了参数（部分提供方增量流出工具调用）：
                // 重播同一 callId 的调用事件，前端按 id 合并即可补上参数。
                _announcedArgumentCounts[callId] = arguments.Count;
                scope.AppendToolCall(call.Name, callId, arguments);
                yield return new AgentStreamEvent
                {
                    Type = AgentStreamEventType.ToolCall,
                    ToolName = call.Name,
                    ToolCallId = callId,
                    ToolArguments = arguments
                };
            }
        }

        // Emit tool results immediately so clients do not need to reload history.
        foreach (FunctionResultContent result in contents.OfType<FunctionResultContent>())
        {
            string? callId = result.CallId;
            if (string.IsNullOrWhiteSpace(callId))
            {
                // 无编号结果按播报顺序回填到最早的未匹配调用，前端才能拿到名字。
                callId = _announcedCallOrder.FirstOrDefault(id => !_matchedCallIds.Contains(id));
            }
            if (!string.IsNullOrWhiteSpace(callId))
            {
                _matchedCallIds.Add(callId);
            }
            string? toolName = string.IsNullOrWhiteSpace(callId)
                ? null
                : _toolCallNames.GetValueOrDefault(callId);
            string? rendered = ToolResultText.Render(result.Result);
            scope.AppendToolResult(callId, rendered);
            yield return new AgentStreamEvent
            {
                Type = AgentStreamEventType.ToolResult,
                ToolCallId = callId,
                // Not every provider pairs a result with a streamed call announcement;
                // carry the name so clients can label the activity instead of a
                // generic placeholder.
                ToolName = toolName,
                Content = rendered
            };
            // update_plan 的结果就是最新计划快照：附加 PlanUpdated 事件让前端
            // 直接渲染任务清单，无需解析通用工具结果。
            if (toolName == "update_plan" && rendered != null)
            {
                yield return new AgentStreamEvent
                {
                    Type = AgentStreamEventType.PlanUpdated,
                    Content = rendered
                };
            }
        }

        foreach (TextReasoningContent reasoning in contents.OfType<TextReasoningContent>())
        {
            if (!string.IsNullOrEmpty(reasoning.Text))
            {
                // Preserve partial reasoning when a streaming request is interrupted.
                scope.AppendPartialReasoning(reasoning.Text);
                yield return new AgentStreamEvent
                {
                    Type = AgentStreamEventType.Reasoning,
                    Content = reasoning.Text
                };
            }
        }

        string content = string.Concat(contents.OfType<TextContent>().Select(item => item.Text));
        if (!string.IsNullOrEmpty(content))
        {
            scope.AppendPartial(content);
            yield return new AgentStreamEvent
            {
                Type = AgentStreamEventType.Content,
                Content = content
            };
        }

        Usage = AgentResponseAdapter.ReadUsage(contents) ?? Usage;
        // Show the model selected for this message (the resolved profile) rather
        // than provider-echoed aliases; fall back to the echo only if the profile
        // carries no model id.
        if (string.IsNullOrWhiteSpace(ModelId))
        {
            ModelId = AgentResponseAdapter.ReadModelId(update.RawRepresentation, ModelId);
        }
    }
}
