using Microsoft.Extensions.Options;
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
        ContextSummary? manual = record.ContextSummaries.LastOrDefault(summary =>
            string.Equals(summary.Trigger, "Manual", StringComparison.Ordinal)
            && string.Equals(summary.Status, "Succeeded", StringComparison.Ordinal)
            && summary.CompactedMessages.Count > 0);
        if (manual == null)
        {
            return record.Messages.AsReadOnly();
        }

        return manual.CompactedMessages
            .Concat(record.Messages.Where(message => message.Sequence > manual.SourceEndSequence))
            .ToList()
            .AsReadOnly();
    }

    internal async Task SaveAsync(
        ConversationContext context,
        int expectedVersion,
        IReadOnlyList<ConversationMessage> messages,
        ConversationStatus status,
        CancellationToken cancellationToken)
    {
        if (!context.IsValid)
        {
            return;
        }

        if (messages.Count > 0)
        {
            AppendResult append = await _store.AppendMessagesAsync(
                context.TenantId!,
                context.ConversationId!,
                expectedVersion,
                messages,
                cancellationToken).ConfigureAwait(false);
            if (!append.Success)
            {
                append = await RetryAppendAsync(context, messages, cancellationToken).ConfigureAwait(false);
            }

            if (!append.Success)
            {
                throw new InvalidOperationException(
                    $"Conversation append failed: {append.ConflictReason}");
            }

            expectedVersion = append.NewVersion;
        }

        if (status != ConversationStatus.Running)
        {
            bool updated = await _store.UpdateStatusAsync(
                context.TenantId!,
                context.ConversationId!,
                status,
                expectedVersion,
                cancellationToken).ConfigureAwait(false);
            if (!updated)
            {
                throw new InvalidOperationException(
                    $"Conversation status update failed: {status}");
            }
        }
    }

    internal static ConversationMessage Message(
        int sequence,
        string role,
        string content,
        string? toolCallId = null,
        string? toolName = null,
        IReadOnlyDictionary<string, string>? metadata = null,
        IReadOnlyList<string>? fileIds = null,
        TokenUsage? tokenUsage = null,
        string? modelId = null) => new()
        {
            MessageId = Guid.NewGuid().ToString("N"),
            Sequence = sequence,
            Role = role,
            Content = content,
            ToolCallId = toolCallId,
            ToolName = toolName,
            Timestamp = DateTimeOffset.UtcNow,
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

        List<ConversationMessage> resequenced = messages
            .Select((message, index) => Message(
                current.MessageCount + index + 1,
                message.Role,
                message.Content,
                message.ToolCallId,
                message.ToolName,
                message.Metadata,
                message.FileIds,
                message.TokenUsage,
                message.ModelId))
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
