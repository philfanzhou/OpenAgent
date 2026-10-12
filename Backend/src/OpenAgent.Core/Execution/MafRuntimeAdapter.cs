using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Runtime;
using OpenAgent.Core.Mapping;
using PlatformAgentResponse = OpenAgent.Contracts.Requests.AgentResponse;

namespace OpenAgent.Core.Execution;

internal sealed class MafRuntimeAdapter(AgentFactory agents) : IRuntimeAdapter
{
    private static readonly IDictionary<string, object?> EmptyToolArguments = new Dictionary<string, object?>();
    public string Id => "maf";
    public IReadOnlyList<RuntimeDescriptor> Describe() => [new("maf", null, "Microsoft Agent Framework", SupportsCodeExecutionToggle: true)];

    public async Task<PlatformAgentResponse> ExecuteAsync(RuntimeInvocation invocation, CancellationToken cancellationToken)
    {
        var profile = invocation.Profile;
        var turn = invocation.Turn;
        var user = invocation.User;
        var executionRequest = invocation.Request;
        await using AgentExecutionScope scope = await agents.CreateAsync(
            profile,
            turn,
            user,
            executionRequest.Query,
            invocation.Files,
            cancellationToken).ConfigureAwait(false);
        AgentSession session = await scope.Agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        ChatMessage userMessage = await scope.CreateUserMessageAsync(cancellationToken).ConfigureAwait(false);
        Microsoft.Agents.AI.AgentResponse response = await scope.Agent.RunAsync(
            userMessage,
            session,
            options: null,
            cancellationToken).ConfigureAwait(false);
        TokenUsage? usage = AgentResponseAdapter.ConvertUsage(response.Usage);
        // Report the model selected for this message; only fall back to the
        // provider echo when the resolved profile carries no model id.
        string modelId = string.IsNullOrWhiteSpace(profile.Model.ModelId)
            ? AgentResponseAdapter.ReadModelId(response.RawRepresentation, profile.Model.ModelId)
            : profile.Model.ModelId;
        await scope.CompleteAsync(usage, modelId, cancellationToken).ConfigureAwait(false);
        return new PlatformAgentResponse
        {
            Content = response.Text ?? string.Empty,
            TokenUsage = usage,
            ModelId = modelId,
            TraceId = turn.TraceId,
            Success = true
        };
    }

    public async IAsyncEnumerable<AgentStreamEvent> ExecuteStreamingAsync(
        RuntimeInvocation invocation,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var profile = invocation.Profile;
        var turn = invocation.Turn;
        var user = invocation.User;
        var executionRequest = invocation.Request;
        await using AgentExecutionScope scope = await agents.CreateAsync(
            profile,
            turn,
            user,
            executionRequest.Query,
            invocation.Files,
            cancellationToken).ConfigureAwait(false);
        AgentSession session = await scope.Agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        ChatMessage userMessage = await scope.CreateUserMessageAsync(cancellationToken).ConfigureAwait(false);
        HashSet<string> announcedToolCalls = new(StringComparer.Ordinal);
        Dictionary<string, string> toolCallNames = new(StringComparer.Ordinal);
        // 无 CallId 的结果按播报顺序配对：记录已播报调用的顺序与已匹配编号，
        // 让每个工具结果都能回填到带名字与参数的调用行上。
        List<string> announcedCallOrder = [];
        HashSet<string> matchedCallIds = new(StringComparer.Ordinal);
        Dictionary<string, int> announcedArgumentCounts = new(StringComparer.Ordinal);
        int syntheticCallCounter = 0;
        TokenUsage? usage = null;
        string modelId = profile.Model.ModelId;
        IAsyncEnumerable<AgentResponseUpdate> updates = scope.Agent.RunStreamingAsync(
            userMessage,
            session,
            options: null,
            cancellationToken);
        await foreach (AgentResponseUpdate update in updates.WithCancellation(cancellationToken))
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
                    ? $"stream_{syntheticCallCounter++}_{call.Name}"
                    : call.CallId;
                // 部分 LLM 返回的工具调用 arguments 为 null 而非空对象：统一规格化为
                // 空字典，播报与持久化都不再出现 null 参数形态。
                IDictionary<string, object?> arguments = call.Arguments ?? EmptyToolArguments;
                string key = string.IsNullOrWhiteSpace(call.CallId) ? call.Name : call.CallId;
                if (announcedToolCalls.Add(key))
                {
                    toolCallNames[callId] = call.Name;
                    announcedCallOrder.Add(callId);
                    announcedArgumentCounts[callId] = arguments.Count;
                    scope.AppendToolCall(call.Name, callId, arguments);
                    yield return new AgentStreamEvent
                    {
                        Type = AgentStreamEventType.ToolCall,
                        ToolName = call.Name,
                        ToolCallId = callId,
                        ToolArguments = arguments
                    };
                }
                else if (string.Equals(callId, key, StringComparison.Ordinal)
                    && arguments is { Count: > 0 }
                    && announcedArgumentCounts.GetValueOrDefault(callId) is not > 0)
                {
                    // 同一调用的后续更新补全了参数（部分提供方增量流出工具调用）：
                    // 重播同一 callId 的调用事件，前端按 id 合并即可补上参数。
                    announcedArgumentCounts[callId] = arguments.Count;
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
                    callId = announcedCallOrder.FirstOrDefault(id => !matchedCallIds.Contains(id));
                }
                if (!string.IsNullOrWhiteSpace(callId))
                {
                    matchedCallIds.Add(callId);
                }
                string? toolName = string.IsNullOrWhiteSpace(callId)
                    ? null
                    : toolCallNames.GetValueOrDefault(callId);
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

            usage = AgentResponseAdapter.ReadUsage(contents) ?? usage;
            // Show the model selected for this message (the resolved profile) rather
            // than provider-echoed aliases; fall back to the echo only if the profile
            // carries no model id.
            if (string.IsNullOrWhiteSpace(modelId))
            {
                modelId = AgentResponseAdapter.ReadModelId(update.RawRepresentation, modelId);
            }
        }

        await scope.CompleteAsync(usage, modelId, cancellationToken).ConfigureAwait(false);
        yield return new AgentStreamEvent
        {
            Type = AgentStreamEventType.Usage,
            Usage = usage,
            ModelId = modelId,
            TraceId = turn.TraceId
        };
    }

}
