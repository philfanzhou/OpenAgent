using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Requests;
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
        string traceId = ResolveTraceId(request.TraceId);
        string agentId = await ResolveAgentIdAsync(
            request,
            user,
            cancellationToken).ConfigureAwait(false);
        AgentRuntimeProfile profile = await _runtime.ResolveAsync(
            agentId,
            user,
            cancellationToken).ConfigureAwait(false);
        AgentRequest executionRequest = CopyWithResolvedValues(request, agentId, traceId);
        if (executionRequest.FileIds.Count > 0)
        {
            await _agents.EnsureConversationAsync(
                agentId,
                executionRequest,
                user,
                cancellationToken).ConfigureAwait(false);
        }
        ResolvedFileRequest resolvedFiles = await _files.ResolveAsync(
            executionRequest,
            user,
            cancellationToken).ConfigureAwait(false);

        await using AgentExecutionScope scope = await _agents.CreateAsync(
            profile,
            executionRequest,
            user,
            resolvedFiles.Files,
            cancellationToken).ConfigureAwait(false);
        AgentSession session = await scope.Agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        ChatMessage userMessage = AgentMessageAdapter.CreateUser(
            executionRequest.Query,
            resolvedFiles.Files);
        Microsoft.Agents.AI.AgentResponse response = await scope.Agent.RunAsync(
            userMessage,
            session,
            options: null,
            cancellationToken).ConfigureAwait(false);
        TokenUsage? usage = AgentResponseAdapter.ConvertUsage(response.Usage);
        string modelId = AgentResponseAdapter.ReadModelId(
            response.RawRepresentation,
            profile.Model.ModelId);
        await scope.CompleteAsync(usage, modelId, cancellationToken).ConfigureAwait(false);
        measurement.Complete(usage);
        return new PlatformAgentResponse
        {
            Content = response.Text ?? string.Empty,
            TokenUsage = usage,
            ModelId = modelId,
            TraceId = traceId,
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
        string traceId = ResolveTraceId(request.TraceId);
        string agentId = await ResolveAgentIdAsync(
            request,
            user,
            cancellationToken).ConfigureAwait(false);
        AgentRuntimeProfile profile = await _runtime.ResolveAsync(
            agentId,
            user,
            cancellationToken).ConfigureAwait(false);
        AgentRequest executionRequest = CopyWithResolvedValues(request, agentId, traceId);
        if (executionRequest.FileIds.Count > 0)
        {
            await _agents.EnsureConversationAsync(
                agentId,
                executionRequest,
                user,
                cancellationToken).ConfigureAwait(false);
        }
        ResolvedFileRequest resolvedFiles = await _files.ResolveAsync(
            executionRequest,
            user,
            cancellationToken).ConfigureAwait(false);

        await using AgentExecutionScope scope = await _agents.CreateAsync(
            profile,
            executionRequest,
            user,
            resolvedFiles.Files,
            cancellationToken).ConfigureAwait(false);
        AgentSession session = await scope.Agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        ChatMessage userMessage = AgentMessageAdapter.CreateUser(
            executionRequest.Query,
            resolvedFiles.Files);
        HashSet<string> announcedToolCalls = new(StringComparer.Ordinal);
        Dictionary<string, string> toolNames = new(StringComparer.Ordinal);
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

                string key = string.IsNullOrWhiteSpace(call.CallId) ? call.Name : call.CallId;
                if (!string.IsNullOrWhiteSpace(call.CallId))
                {
                    toolNames[call.CallId] = call.Name;
                }
                if (announcedToolCalls.Add(key))
                {
                    yield return new AgentStreamEvent
                    {
                        Type = AgentStreamEventType.ToolCall,
                        ToolName = call.Name,
                        ToolCallId = call.CallId,
                        ToolArguments = call.Arguments
                    };
                }
            }

            foreach (FunctionResultContent result in contents.OfType<FunctionResultContent>())
            {
                yield return new AgentStreamEvent
                {
                    Type = AgentStreamEventType.ToolResult,
                    ToolName = !string.IsNullOrWhiteSpace(result.CallId)
                        ? toolNames.GetValueOrDefault(result.CallId)
                        : null,
                    ToolCallId = result.CallId,
                    ToolResult = result.Exception?.Message ?? result.Result?.ToString() ?? string.Empty
                };
            }

            foreach (TextReasoningContent reasoning in contents.OfType<TextReasoningContent>())
            {
                if (!string.IsNullOrEmpty(reasoning.Text))
                {
                    // 累积思考内容，保证流式中止（暂停）时也能把思考过程持久化进会话历史。
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
            modelId = AgentResponseAdapter.ReadModelId(update.RawRepresentation, modelId);
        }

        await scope.CompleteAsync(usage, modelId, cancellationToken).ConfigureAwait(false);
        measurement.Complete(usage);
        yield return new AgentStreamEvent
        {
            Type = AgentStreamEventType.Usage,
            Usage = usage,
            ModelId = modelId
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

    private static string ResolveTraceId(string? traceId) =>
        string.IsNullOrWhiteSpace(traceId) ? Guid.NewGuid().ToString("N") : traceId;

    private static AgentRequest CopyWithResolvedValues(
        AgentRequest request,
        string agentId,
        string traceId) => new()
        {
            Query = request.Query,
            AgentId = agentId,
            ConversationId = request.ConversationId,
            ConversationType = request.ConversationType,
            TraceId = traceId,
            ClientType = request.ClientType,
            IdempotencyKey = request.IdempotencyKey,
            ExternalContext = request.ExternalContext,
            FileIds = request.FileIds
        };
}
