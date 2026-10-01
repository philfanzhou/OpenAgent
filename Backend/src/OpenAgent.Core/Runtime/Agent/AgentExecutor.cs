using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAgent.Contracts.Configuration;
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
        PreparedExecution prepared = await PrepareAsync(request, user, cancellationToken).ConfigureAwait(false);
        await using AgentExecutionScope scope = prepared.Scope;
        TurnContext turn = prepared.Turn;
        AgentRuntimeProfile profile = prepared.Profile;
        AgentSession session = prepared.Session;
        ChatMessage userMessage = prepared.UserMessage;
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
        PreparedExecution prepared = await PrepareAsync(request, user, cancellationToken).ConfigureAwait(false);
        await using AgentExecutionScope scope = prepared.Scope;
        TurnContext turn = prepared.Turn;
        AgentRuntimeProfile profile = prepared.Profile;
        AgentSession session = prepared.Session;
        ChatMessage userMessage = prepared.UserMessage;
        StreamEventAdapter adapter = new(scope, profile.Model.ModelId);
        IAsyncEnumerable<AgentResponseUpdate> updates = scope.Agent.RunStreamingAsync(
            userMessage,
            session,
            options: null,
            cancellationToken);
        await foreach (AgentResponseUpdate update in updates.WithCancellation(cancellationToken))
        {
            foreach (AgentStreamEvent streamEvent in adapter.Adapt(update))
            {
                yield return streamEvent;
            }
        }

        await scope.CompleteAsync(adapter.Usage, adapter.ModelId, cancellationToken).ConfigureAwait(false);
        measurement.Complete(adapter.Usage);
        yield return new AgentStreamEvent
        {
            Type = AgentStreamEventType.Usage,
            Usage = adapter.Usage,
            ModelId = adapter.ModelId,
            TraceId = turn.TraceId
        };
    }

    private async Task<PreparedExecution> PrepareAsync(
        AgentRequest request,
        IAgentUserContext user,
        CancellationToken cancellationToken)
    {
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
        if (executionRequest.FileIds.Count > 0)
        {
            await _agents.EnsureConversationAsync(
                turn,
                executionRequest.Query,
                cancellationToken).ConfigureAwait(false);
        }
        ResolvedFileRequest resolvedFiles = await _files.ResolveAsync(
            executionRequest,
            user,
            cancellationToken).ConfigureAwait(false);

        AgentExecutionScope scope = await _agents.CreateAsync(
            profile,
            turn,
            user,
            executionRequest.Query,
            resolvedFiles.Files,
            cancellationToken).ConfigureAwait(false);
        try
        {
            AgentSession session = await scope.Agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
            ChatMessage userMessage = await scope.CreateUserMessageAsync(cancellationToken).ConfigureAwait(false);
            return new PreparedExecution(turn, profile, scope, session, userMessage);
        }
        catch
        {
            await scope.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private sealed record PreparedExecution(
        TurnContext Turn,
        AgentRuntimeProfile Profile,
        AgentExecutionScope Scope,
        AgentSession Session,
        ChatMessage UserMessage);

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
