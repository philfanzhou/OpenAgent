using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Runtime;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Conversation;
using OpenAgent.Core.Files;
using PlatformAgentResponse = OpenAgent.Contracts.Requests.AgentResponse;

namespace OpenAgent.Core.Runtime.Agent;

public sealed class AgentExecutor
{
    private const string DefaultAgentId = "default";

    private readonly IAgentRuntimeResolver _runtime;
    private readonly AgentFactory _agents;
    private readonly ConversationAgentResolver _conversationAgents;
    private readonly FileAssetRequestResolver _files;

    /// <summary>null 参数的统一替代形态：所有工具调用播报至少是空对象。</summary>
    private static readonly IDictionary<string, object?> EmptyToolArguments = new Dictionary<string, object?>();

    internal AgentExecutor(
        IAgentRuntimeResolver runtime,
        AgentFactory agents,
        ConversationAgentResolver conversationAgents,
        FileAssetRequestResolver files)
    {
        _runtime = runtime;
        _agents = agents;
        _conversationAgents = conversationAgents;
        _files = files;
    }

    public async Task<PlatformAgentResponse> ExecuteAsync(
        AgentRequest request,
        IAgentUserContext user,
        CancellationToken cancellationToken)
    {
        using EngineMeter.EngineExecutionMeasurement measurement = EngineMeter.StartAgentCall("sync");
        EnsureRequest(request);
        string agentId = await ResolveAgentIdAsync(
            request,
            user,
            cancellationToken).ConfigureAwait(false);
        TurnContext turn = CreateTurn(request, user, agentId);
        AgentRuntimeProfile profile = await _runtime.ResolveAsync(
            agentId,
            RequireLlmProfileId(request),
            user,
            cancellationToken).ConfigureAwait(false);
        AgentRequest executionRequest = CopyWithResolvedValues(request, turn);
        ResolvedFileRequest resolvedFiles = await _files.ResolveAsync(
            executionRequest,
            user,
            cancellationToken).ConfigureAwait(false);

        await using AgentExecutionScope scope = await _agents.CreateAsync(
            profile,
            turn,
            user,
            executionRequest.Query,
            resolvedFiles.Files,
            cancellationToken).ConfigureAwait(false);
        await scope.PrepareForAgentSessionAsync(cancellationToken).ConfigureAwait(false);
        AgentSession session = await _agents.CreateSessionAsync(
            scope.Agent, turn, profile, user, cancellationToken).ConfigureAwait(false);
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
        JsonElement serializedSession = await scope.Agent.SerializeSessionAsync(
            session,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await scope.CompleteAsync(
            usage,
            modelId,
            new AgentSessionSnapshot(
                serializedSession.GetRawText(),
                AgentSessionFingerprint.Create(profile)),
            cancellationToken).ConfigureAwait(false);
        measurement.Complete(usage);
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
        AgentRequest request,
        IAgentUserContext user,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using EngineMeter.EngineExecutionMeasurement measurement = EngineMeter.StartAgentCall("stream");
        EnsureRequest(request);
        string agentId = await ResolveAgentIdAsync(
            request,
            user,
            cancellationToken).ConfigureAwait(false);
        TurnContext turn = CreateTurn(request, user, agentId);
        AgentRuntimeProfile profile = await _runtime.ResolveAsync(
            agentId,
            RequireLlmProfileId(request),
            user,
            cancellationToken).ConfigureAwait(false);
        AgentRequest executionRequest = CopyWithResolvedValues(request, turn);
        ResolvedFileRequest resolvedFiles = await _files.ResolveAsync(
            executionRequest,
            user,
            cancellationToken).ConfigureAwait(false);

        await using AgentExecutionScope scope = await _agents.CreateAsync(
            profile,
            turn,
            user,
            executionRequest.Query,
            resolvedFiles.Files,
            cancellationToken).ConfigureAwait(false);
        await scope.PrepareForAgentSessionAsync(cancellationToken).ConfigureAwait(false);
        AgentSession session = await _agents.CreateSessionAsync(
            scope.Agent, turn, profile, user, cancellationToken).ConfigureAwait(false);
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

        JsonElement serializedSession = await scope.Agent.SerializeSessionAsync(
            session,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await scope.CompleteAsync(
            usage,
            modelId,
            new AgentSessionSnapshot(
                serializedSession.GetRawText(),
                AgentSessionFingerprint.Create(profile)),
            cancellationToken).ConfigureAwait(false);
        measurement.Complete(usage);
        yield return new AgentStreamEvent
        {
            Type = AgentStreamEventType.Usage,
            Usage = usage,
            ModelId = modelId,
            TraceId = turn.TraceId
        };
    }

    private static void EnsureRequest(AgentRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
        {
            throw new AgentException(
                AgentErrorCode.MissingRequiredField,
                "Query is required");
        }
    }

    private async Task<string> ResolveAgentIdAsync(
        AgentRequest request,
        IAgentUserContext user,
        CancellationToken cancellationToken)
    {
        string? resolvedAgentId = await _conversationAgents.ResolveAsync(
            request,
            user,
            cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(resolvedAgentId)
            ? DefaultAgentId
            : resolvedAgentId;
    }

    /// <summary>
    /// TurnContext 的唯一构造点：这里完成 traceId 兜底与租户归一化，
    /// 下游（AgentFactory/历史/文件）一律消费已解析的值，不再各自兜底。
    /// </summary>
    private static TurnContext CreateTurn(AgentRequest request, IAgentUserContext user, string agentId) => new()
    {
        TraceId = string.IsNullOrWhiteSpace(request.TraceId)
            ? Guid.NewGuid().ToString("N")
            : request.TraceId,
        TenantId = user.TenantId ?? string.Empty,
        UserId = user.UserId,
        ConversationId = ResolveConversationId(request),
        AgentId = agentId,
        ConversationType = request.ConversationType
    };

    private static AgentRequest CopyWithResolvedValues(AgentRequest request, TurnContext turn) => new()
    {
        Query = request.Query,
        AgentId = turn.AgentId,
        LlmProfileId = request.LlmProfileId,
        ConversationId = turn.ConversationId,
        ConversationType = request.ConversationType,
        TraceId = turn.TraceId,
        ClientType = request.ClientType,
        IdempotencyKey = request.IdempotencyKey,
        ExternalContext = request.ExternalContext,
        FileIds = request.FileIds
    };

    private static string RequireLlmProfileId(AgentRequest request) =>
        !string.IsNullOrWhiteSpace(request.LlmProfileId)
            ? request.LlmProfileId
            : throw new AgentException(
                AgentErrorCode.MissingRequiredField,
                "LlmProfileId is required");

    private static string? ResolveConversationId(AgentRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.ConversationId))
        {
            return request.ConversationId;
        }

        // Direct callers may omit a conversation id on the first request. Files
        // still need a conversation-scoped reference before they can be read.
        return request.FileIds.Count > 0
            ? Guid.NewGuid().ToString("N")
            : null;
    }
}
