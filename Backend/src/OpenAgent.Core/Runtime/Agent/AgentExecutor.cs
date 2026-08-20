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

    private readonly AgentModelResolver _models;
    private readonly AgentFactory _agents;
    private readonly ConversationAgentResolver _conversationAgents;
    private readonly FileAssetRequestResolver _files;

    internal AgentExecutor(
        IAgentRuntimeResolver runtime,
        AgentFactory agents,
        ConversationAgentResolver conversationAgents,
        FileAssetRequestResolver files)
    {
        _models = new AgentModelResolver(runtime);
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
        (string agentId, LlmModelSelection? conversationModel) = await ResolveAgentAsync(
            request,
            user,
            cancellationToken).ConfigureAwait(false);
        AgentModelResolution modelResolution = await _models.ResolveAsync(
            agentId,
            request,
            conversationModel,
            user,
            cancellationToken).ConfigureAwait(false);
        AgentRuntimeProfile profile = modelResolution.Profile;
        AgentRequest executionRequest = CopyWithResolvedValues(
            request,
            agentId,
            traceId,
            modelResolution);
        if (executionRequest.FileIds.Count > 0
            || executionRequest.UpdateConversationModelOverride)
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
        (string agentId, LlmModelSelection? conversationModel) = await ResolveAgentAsync(
            request,
            user,
            cancellationToken).ConfigureAwait(false);
        AgentModelResolution modelResolution = await _models.ResolveAsync(
            agentId,
            request,
            conversationModel,
            user,
            cancellationToken).ConfigureAwait(false);
        AgentRuntimeProfile profile = modelResolution.Profile;
        AgentRequest executionRequest = CopyWithResolvedValues(
            request,
            agentId,
            traceId,
            modelResolution);
        if (executionRequest.FileIds.Count > 0
            || executionRequest.UpdateConversationModelOverride)
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

    private async Task<(string AgentId, LlmModelSelection? ConversationModel)> ResolveAgentAsync(
        AgentRequest request,
        IAgentUserContext user,
        CancellationToken cancellationToken)
    {
        ConversationResolution resolution = await _conversationAgents.ResolveContextAsync(
            request,
            user,
            cancellationToken).ConfigureAwait(false);
        string agentId = string.IsNullOrWhiteSpace(resolution.AgentId)
            ? DefaultAgentId
            : resolution.AgentId;
        return (agentId, resolution.ModelOverride);
    }

    private static string ResolveTraceId(string? traceId) =>
        string.IsNullOrWhiteSpace(traceId) ? Guid.NewGuid().ToString("N") : traceId;

    private static AgentRequest CopyWithResolvedValues(
        AgentRequest request,
        string agentId,
        string traceId,
        AgentModelResolution modelResolution) => new()
        {
            Query = request.Query,
            AgentId = agentId,
            ConversationId = request.ConversationId,
            ConversationType = request.ConversationType,
            TraceId = traceId,
            ClientType = request.ClientType,
            IdempotencyKey = request.IdempotencyKey,
            ConversationModelOverride = modelResolution.ConversationModel,
            UpdateConversationModelOverride = modelResolution.ApplyConversationUpdate,
            MessageModelOverride = request.MessageModelOverride,
            ModelSelectionSource = modelResolution.Source,
            ExternalContext = request.ExternalContext,
            FileIds = request.FileIds
        };
}
