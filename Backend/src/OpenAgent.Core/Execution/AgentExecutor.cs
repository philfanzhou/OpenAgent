using System.Runtime.CompilerServices;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Runtime;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Conversation;
using OpenAgent.Core.Execution;
using OpenAgent.Core.Files.Requests;
using OpenAgent.Core.Observability;

namespace OpenAgent.Core.Runtime.Agent;

public sealed class AgentExecutor
{
    private const string DefaultAgentId = "default";
    private readonly IAgentRuntimeResolver _runtime;
    private readonly AgentFactory _agents;
    private readonly ConversationAgentResolver _conversationAgents;
    private readonly FileAssetRequestResolver _files;
    private readonly RuntimeRegistry _adapters;
    private readonly IConversationStore _store;

    internal AgentExecutor(IAgentRuntimeResolver runtime, AgentFactory agents,
        ConversationAgentResolver conversationAgents, FileAssetRequestResolver files,
        RuntimeRegistry adapters, IConversationStore store)
    {
        _runtime = runtime;
        _agents = agents;
        _conversationAgents = conversationAgents;
        _files = files;
        _adapters = adapters;
        _store = store;
    }

    public async Task<AgentResponse> ExecuteAsync(AgentRequest request, IAgentUserContext user, CancellationToken cancellationToken)
    {
        using var measurement = EngineMeter.StartAgentCall("sync");
        RuntimeInvocation invocation = await PrepareAsync(request, user, cancellationToken).ConfigureAwait(false);
        AgentResponse response = await _adapters.Resolve(invocation.Profile.Config.Runtime)
            .ExecuteAsync(invocation, cancellationToken).ConfigureAwait(false);
        measurement.Complete(response.TokenUsage);
        return response;
    }

    public async IAsyncEnumerable<AgentStreamEvent> ExecuteStreamingAsync(AgentRequest request,
        IAgentUserContext user, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var measurement = EngineMeter.StartAgentCall("stream");
        RuntimeInvocation invocation = await PrepareAsync(request, user, cancellationToken).ConfigureAwait(false);
        await foreach (AgentStreamEvent item in _adapters.Resolve(invocation.Profile.Config.Runtime)
            .ExecuteStreamingAsync(invocation, cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (item.Type == AgentStreamEventType.Usage)
                measurement.Complete(item.Usage);
            yield return item;
        }
    }

    private async Task<RuntimeInvocation> PrepareAsync(AgentRequest request, IAgentUserContext user, CancellationToken cancellationToken)
    {
        EnsureRequest(request);
        string agentId = await ResolveAgentIdAsync(request, user, cancellationToken).ConfigureAwait(false);
        TurnContext turn = CreateTurn(request, user, agentId);
        AgentRuntimeProfile profile = await _runtime.ResolveAsync(agentId, RequireLlmProfileId(request), user, cancellationToken).ConfigureAwait(false);
        _adapters.Resolve(profile.Config.Runtime);
        if (turn.ConversationId != null)
        {
            ConversationRecord? record = await _store.GetRecordAsync(turn.TenantId, turn.ConversationId, cancellationToken).ConfigureAwait(false);
            if (record != null)
            {
                if (record.IsDeletedByUser || record.UserId != user.UserId || record.Type != request.ConversationType)
                    throw new AgentException(AgentErrorCode.PermissionDenied, "Conversation does not belong to the current user");
                if (record.Messages.Count > 0)
                {
                    Dictionary<string, string>? binding = record.Messages.FirstOrDefault(m => m.Role == "user")?.Metadata?.Extensions;
                    string adapter = binding?.GetValueOrDefault("runtime.adapter") ?? "maf";
                    string? harness = binding?.GetValueOrDefault("runtime.harness");
                    if (adapter != profile.Config.Runtime.Adapter || harness != profile.Config.Runtime.Harness)
                        throw new AgentException(AgentErrorCode.Conflict, "The conversation is bound to another runtime. Start a new conversation.");
                }
            }
        }
        AgentRequest executionRequest = CopyWithResolvedValues(request, turn);
        if (executionRequest.FileIds.Count > 0)
            await _agents.EnsureConversationAsync(turn, executionRequest.Query, cancellationToken).ConfigureAwait(false);
        ResolvedFileRequest files = await _files.ResolveAsync(executionRequest, user, cancellationToken).ConfigureAwait(false);
        return new RuntimeInvocation(executionRequest, profile, turn, user, files.Files);
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
