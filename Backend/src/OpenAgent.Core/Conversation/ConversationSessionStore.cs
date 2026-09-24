using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.Agents.AI;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Core.Conversation;

/// <summary>
/// The single persistence boundary used by an agent session. It owns creation,
/// optimistic append retry and status transitions; callers do not coordinate
/// separate loader and saver services.
/// </summary>
internal sealed class ConversationSessionStore
{
    private readonly IConversationStore _store;
    private readonly ConversationStoreOptions _options;

    public ConversationSessionStore(
        IConversationStore store,
        IOptions<ConversationStoreOptions> options)
    {
        _store = store;
        _options = options.Value;
    }

    internal IConversationStore Store => _store;

    internal async Task<AgentSession?> RestoreAgentSessionAsync(
        AIAgent agent,
        ConversationContext context,
        string agentId,
        string userId,
        string configFingerprint,
        CancellationToken cancellationToken)
    {
        if (!context.IsValid)
        {
            return null;
        }

        ConversationRecord? record = await _store.GetRecordAsync(
            context.TenantId!,
            context.ConversationId!,
            cancellationToken).ConfigureAwait(false);
        if (record == null
            || !string.Equals(record.UserId, userId, StringComparison.Ordinal)
            || (!string.IsNullOrWhiteSpace(record.AgentId)
                && !string.Equals(record.AgentId, agentId, StringComparison.Ordinal))
            || record.IsDeletedByUser)
        {
            throw new AgentException(
                AgentErrorCode.PermissionDenied,
                "Conversation does not belong to the current user or agent");
        }

        AgentSessionSnapshot? snapshot = await _store.GetAgentSessionSnapshotAsync(
            context.TenantId!,
            userId,
            context.ConversationId!,
            agentId,
            cancellationToken).ConfigureAwait(false);
        if (snapshot == null
            || snapshot.FormatVersion != 1
            || !string.Equals(snapshot.ConfigFingerprint, configFingerprint, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(snapshot.StateJson);
            return await agent.DeserializeSessionAsync(
                document.RootElement,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException
                or ArgumentException
                or InvalidOperationException
                or NotSupportedException)
        {
            return null;
        }
    }

    internal async Task<ConversationSession> OpenAsync(
        ConversationContext context,
        string resolvedAgentId,
        string input,
        CancellationToken cancellationToken)
    {
        ConversationRecord? record = await _store.GetRecordAsync(
            context.TenantId!,
            context.ConversationId!,
            cancellationToken).ConfigureAwait(false);
        if (record == null)
        {
            record = CreateRecord(context, resolvedAgentId, input);
            if (!await _store.CreateAsync(record, cancellationToken).ConfigureAwait(false))
            {
                record = await _store.GetRecordAsync(
                    context.TenantId!,
                    context.ConversationId!,
                    cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException(
                        "Conversation could not be created or reloaded.");
            }
        }

        if (!string.Equals(record.TenantId, context.TenantId, StringComparison.Ordinal)
            || record.Type != context.Type
            || (!string.IsNullOrWhiteSpace(context.UserId)
                && !string.Equals(record.UserId, context.UserId, StringComparison.Ordinal)))
        {
            throw new AgentException(
                AgentErrorCode.PermissionDenied,
                "Conversation does not belong to the current user");
        }

        return new ConversationSession(
            record.Version,
            record.MessageCount + 1,
            ResolveModelHistory(record));
    }

    internal static IReadOnlyList<ConversationMessage> ResolveModelHistory(ConversationRecord record)
    {
        ContextSummary? latest = record.ContextSummaries.LastOrDefault(summary =>
            string.Equals(summary.Status, "Succeeded", StringComparison.Ordinal)
            && summary.CompactedMessages.Count > 0
            && IsUsableProjection(summary));
        if (latest == null)
        {
            return record.Messages.AsReadOnly();
        }

        IReadOnlyList<ConversationMessage> tail = record.Messages
            .Where(message => message.Sequence > latest.SourceEndSequence)
            .ToList();
        int overlap = FindProjectionOverlap(latest.CompactedMessages, tail);
        return latest.CompactedMessages
            .Concat(tail.Skip(overlap))
            .ToList()
            .AsReadOnly();
    }

    private static bool IsUsableProjection(ContextSummary summary) =>
        !string.IsNullOrWhiteSpace(summary.Summary)
        && !summary.Summary.Contains(
            "[Summary unavailable]",
            StringComparison.OrdinalIgnoreCase);

    private static int FindProjectionOverlap(
        IReadOnlyList<ConversationMessage> projection,
        IReadOnlyList<ConversationMessage> tail)
    {
        int maximum = Math.Min(projection.Count, tail.Count);
        for (int candidate = maximum; candidate > 0; candidate--)
        {
            bool matches = true;
            for (int index = 0; index < candidate; index++)
            {
                if (!Equivalent(
                        projection[projection.Count - candidate + index],
                        tail[index]))
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                return candidate;
            }
        }

        return 0;
    }

    private static bool Equivalent(ConversationMessage left, ConversationMessage right) =>
        string.Equals(left.Role, right.Role, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.Content, right.Content, StringComparison.Ordinal)
        && string.Equals(left.ToolCallId, right.ToolCallId, StringComparison.Ordinal)
        && string.Equals(left.ToolName, right.ToolName, StringComparison.Ordinal);

    internal async Task SaveAsync(
        ConversationContext context,
        int expectedVersion,
        IReadOnlyList<ConversationMessage> messages,
        ConversationStatus status,
        CancellationToken cancellationToken) =>
        await SaveAsync(
            context,
            expectedVersion,
            messages,
            status,
            sessionSnapshot: null,
            cancellationToken).ConfigureAwait(false);

    internal async Task SaveAsync(
        ConversationContext context,
        int expectedVersion,
        IReadOnlyList<ConversationMessage> messages,
        ConversationStatus status,
        AgentSessionSnapshot? sessionSnapshot,
        CancellationToken cancellationToken)
    {
        if (!context.IsValid)
        {
            return;
        }

        // 本轮所有落库消息统一盖章轮次追溯键，前端消息与 LLM 交互日志按 TraceId 对齐。
        if (messages.Count > 0 && !string.IsNullOrWhiteSpace(context.TraceId))
        {
            messages = messages
                .Select(message => message.TraceId == null
                    ? message with { TraceId = context.TraceId }
                    : message)
                .ToList();
        }

        AppendResult commit = await _store.CommitTurnAsync(
            context.TenantId!,
            context.ConversationId!,
            expectedVersion,
            messages,
            status,
            sessionSnapshot,
            cancellationToken).ConfigureAwait(false);
        if (!commit.Success && sessionSnapshot == null)
        {
            ConversationRecord? current = await _store.GetRecordAsync(
                context.TenantId!,
                context.ConversationId!,
                cancellationToken).ConfigureAwait(false);
            if (current != null)
            {
                IReadOnlyList<ConversationMessage> resequenced = messages
                    .Select((message, index) => message with
                    {
                        Sequence = current.MessageCount + index + 1
                    })
                    .ToList()
                    .AsReadOnly();
                commit = await _store.CommitTurnAsync(
                    context.TenantId!,
                    context.ConversationId!,
                    current.Version,
                    resequenced,
                    status,
                    sessionSnapshot: null,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }

        if (!commit.Success)
        {
            throw new InvalidOperationException($"Conversation turn commit failed: {commit.ConflictReason}");
        }
    }

    internal static ConversationMessage Message(
        int sequence,
        string role,
        string content,
        string? toolCallId = null,
        string? toolName = null,
        ConversationMessageMetadata? metadata = null,
        IReadOnlyList<string>? fileIds = null,
        TokenUsage? tokenUsage = null,
        string? modelId = null,
        string? traceId = null) => new()
    {
        MessageId = Guid.NewGuid().ToString("N"),
        Sequence = sequence,
        Role = role,
        Content = content,
        ToolCallId = toolCallId,
        ToolName = toolName,
        Timestamp = DateTimeOffset.UtcNow,
        TraceId = traceId,
        Metadata = metadata,
        FileIds = fileIds ?? Array.Empty<string>(),
        TokenUsage = tokenUsage,
        ModelId = modelId
    };

    private ConversationRecord CreateRecord(
        ConversationContext context,
        string resolvedAgentId,
        string input)
    {
        int titleLength = _options.TitleTruncateLength;
        return new ConversationRecord
        {
            ConversationId = context.ConversationId!,
            TenantId = context.TenantId!,
            UserId = context.UserId ?? "anonymous",
            Type = context.Type,
            AgentId = context.AgentId ?? resolvedAgentId,
            TraceId = context.TraceId,
            Status = ConversationStatus.Running,
            Version = 1,
            MessageCount = 0,
            Title = string.IsNullOrEmpty(input)
                ? null
                : input.Length <= titleLength ? input : input[..titleLength],
            Messages = []
        };
    }

    private async Task<AppendResult> RetryAppendAsync(
        ConversationContext context,
        IReadOnlyList<ConversationMessage> messages,
        CancellationToken cancellationToken)
    {
        ConversationRecord? current = await _store.GetRecordAsync(
            context.TenantId!,
            context.ConversationId!,
            cancellationToken).ConfigureAwait(false);
        if (current == null)
        {
            return AppendResult.Conflict("conversation-not-found");
        }

        // 保留原 MessageId/Timestamp/IdempotencyKey：重试是同一批逻辑消息的重排序，
        // 重新生成标识会让幂等去重失效。
        List<ConversationMessage> resequenced = messages
            .Select((message, index) => message with
            {
                Sequence = current.MessageCount + index + 1,
                TraceId = message.TraceId ?? context.TraceId
            })
            .ToList();
        return await _store.AppendMessagesAsync(
            context.TenantId!,
            context.ConversationId!,
            current.Version,
            resequenced.AsReadOnly(),
            cancellationToken).ConfigureAwait(false);
    }
}

internal sealed record ConversationSession(
    int CurrentVersion,
    int NextSequence,
    IReadOnlyList<ConversationMessage> History);
