using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Core.Runtime.Agent;

namespace OpenAgent.Core.Conversation;

/// <summary>
/// Adds durable audit metadata around an existing MAF compaction strategy.
/// The wrapped strategy remains the only component that changes model context.
/// </summary>
internal sealed class AuditedCompactionStrategy : CompactionStrategy
{
    private readonly CompactionStrategy _strategy;
    private readonly CompactionTrigger _triggerCondition;
    private readonly string _strategyName;
    private readonly string _trigger;
    private readonly ConversationContext _context;
    private readonly IConversationStore _store;
    private readonly ILogger<AuditedCompactionStrategy> _logger;
    private readonly bool _recordUnchanged;

    internal ContextSummary? LastAudit { get; private set; }
    internal bool LastAuditRecorded { get; private set; }

    internal AuditedCompactionStrategy(
        CompactionStrategy strategy,
        CompactionTrigger triggerCondition,
        string strategyName,
        string trigger,
        ConversationContext context,
        IConversationStore store,
        ILogger<AuditedCompactionStrategy> logger,
        bool recordUnchanged)
        : base(CompactionTriggers.Always, CompactionTriggers.Always)
    {
        _strategy = strategy;
        _triggerCondition = triggerCondition;
        _strategyName = strategyName;
        _trigger = trigger;
        _context = context;
        _store = store;
        _logger = logger;
        _recordUnchanged = recordUnchanged;
    }

    protected override async ValueTask<bool> CompactCoreAsync(
        CompactionMessageIndex index,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        List<GroupSnapshot> originalGroups = index.Groups
            .Select(group => new GroupSnapshot(group, group.IsExcluded, group.ExcludeReason))
            .ToList();
        List<ChatMessage> before = index.GetIncludedMessages().ToList();
        bool expectedChange = _triggerCondition(index) && CanCompact(index);
        try
        {
            bool compacted = await _strategy.CompactAsync(
                index,
                logger,
                cancellationToken).ConfigureAwait(false);
            List<ChatMessage> after = index.GetIncludedMessages().ToList();
            int compressedMessageCount = CountRemoved(before, after);
            if (!compacted && expectedChange)
            {
                Restore(index, originalGroups);
                await RecordFailureAsync(
                    "Compaction strategy did not produce a compacted context.",
                    index.IncludedTokenCount).ConfigureAwait(false);
                ConversationCompactionLog.CompactionRecovered(
                    _logger,
                    _context.ConversationId ?? string.Empty,
                    _strategyName,
                    exception: null);
                return false;
            }
            if (compacted || _recordUnchanged)
            {
                string? summary = ReadSummary(index);
                await TryRecordAsync(
                    status: "Succeeded",
                    summary,
                    result: summary ?? $"Omitted {compressedMessageCount} messages; retained {after.Count} messages.",
                    error: null,
                    compressedMessageCount,
                    tokenCount: index.IncludedTokenCount,
                    originalHistoryRestored: false,
                    compactedMessages: after,
                    cancellationToken).ConfigureAwait(false);
            }
            return compacted;
        }
        catch (OperationCanceledException)
        {
            Restore(index, originalGroups);
            throw;
        }
        catch (Exception exception)
        {
            Restore(index, originalGroups);
            await RecordFailureAsync(exception.Message, index.IncludedTokenCount).ConfigureAwait(false);
            ConversationCompactionLog.CompactionRecovered(
                _logger,
                _context.ConversationId ?? string.Empty,
                _strategyName,
                exception);
            return false;
        }
    }

    private Task RecordFailureAsync(string error, int tokenCount) => TryRecordAsync(
        status: "Failed",
        summary: null,
        result: "Original history restored for model invocation.",
        error,
        compressedMessageCount: 0,
        tokenCount,
        originalHistoryRestored: true,
        compactedMessages: null,
        CancellationToken.None);

    private async Task TryRecordAsync(
        string status,
        string? summary,
        string? result,
        string? error,
        int compressedMessageCount,
        int tokenCount,
        bool originalHistoryRestored,
        IReadOnlyList<ChatMessage>? compactedMessages,
        CancellationToken cancellationToken)
    {
        if (!_context.IsValid)
        {
            return;
        }

        try
        {
            ConversationRecord? conversation = await _store.GetRecordAsync(
                _context.TenantId!,
                _context.ConversationId!,
                cancellationToken).ConfigureAwait(false);
            int rangeCount = Math.Min(
                conversation?.MessageCount ?? compressedMessageCount,
                Math.Max(compressedMessageCount, 0));
            if (string.Equals(status, "Failed", StringComparison.Ordinal))
            {
                rangeCount = conversation?.MessageCount ?? 0;
            }

            var audit = new ContextSummary
            {
                CompressionId = Guid.NewGuid().ToString("N"),
                Strategy = _strategyName,
                Trigger = _trigger,
                Status = status,
                Summary = summary,
                Result = result,
                Error = error,
                LastCompressedAt = DateTimeOffset.UtcNow,
                CompressedMessageCount = compressedMessageCount,
                OriginalStartSequence = rangeCount > 0 ? 1 : 0,
                OriginalEndSequence = rangeCount,
                TokenCount = tokenCount,
                OriginalHistoryRestored = originalHistoryRestored,
                SourceEndSequence = conversation?.MessageCount ?? 0,
                CompactedMessages = ToStored(compactedMessages)
            };
            LastAudit = audit;
            bool recorded = await _store.RecordCompressionAsync(
                _context.TenantId!,
                _context.ConversationId!,
                audit,
                cancellationToken).ConfigureAwait(false);
            LastAuditRecorded = recorded;
            if (!recorded)
            {
                ConversationCompactionLog.AuditWriteFailed(
                    _logger,
                    _context.ConversationId!,
                    _strategyName,
                    exception: null);
            }
        }
        catch (Exception exception)
        {
            ConversationCompactionLog.AuditWriteFailed(
                _logger,
                _context.ConversationId!,
                _strategyName,
                exception);
        }
    }

    private static List<ConversationMessage> ToStored(IReadOnlyList<ChatMessage>? messages)
    {
        if (messages == null)
        {
            return [];
        }

        int sequence = 1;
        return AgentMessageAdapter.ToStored(messages, ref sequence).ToList();
    }

    private static int CountRemoved(
        IReadOnlyList<ChatMessage> before,
        IReadOnlyList<ChatMessage> after)
    {
        var retained = new HashSet<ChatMessage>(after, ReferenceComparer.Instance);
        return before.Count(message => !retained.Contains(message));
    }

    private bool CanCompact(CompactionMessageIndex index) => _strategy switch
    {
        SummarizationCompactionStrategy summarization =>
            index.IncludedNonSystemGroupCount > summarization.MinimumPreservedGroups,
        TruncationCompactionStrategy truncation =>
            index.IncludedNonSystemGroupCount > truncation.MinimumPreservedGroups,
        SlidingWindowCompactionStrategy slidingWindow =>
            index.IncludedTurnCount > slidingWindow.MinimumPreservedTurns,
        _ => true
    };

    private static string? ReadSummary(CompactionMessageIndex index) =>
        index.Groups
            .Where(group => !group.IsExcluded && group.Kind == CompactionGroupKind.Summary)
            .SelectMany(group => group.Messages)
            .Select(message => message.Text)
            .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));

    private static void Restore(
        CompactionMessageIndex index,
        IReadOnlyList<GroupSnapshot> originalGroups)
    {
        index.Groups.Clear();
        foreach (GroupSnapshot snapshot in originalGroups)
        {
            snapshot.Group.IsExcluded = snapshot.IsExcluded;
            snapshot.Group.ExcludeReason = snapshot.ExcludeReason;
            index.Groups.Add(snapshot.Group);
        }
    }

    private sealed record GroupSnapshot(
        CompactionMessageGroup Group,
        bool IsExcluded,
        string? ExcludeReason);

    private sealed class ReferenceComparer : IEqualityComparer<ChatMessage>
    {
        internal static ReferenceComparer Instance { get; } = new();

        public bool Equals(ChatMessage? x, ChatMessage? y) => ReferenceEquals(x, y);

        public int GetHashCode(ChatMessage obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}

internal static partial class ConversationCompactionLog
{
    [LoggerMessage(
        EventId = 1450,
        Level = LogLevel.Warning,
        Message = "Conversation compaction failed and original history was restored. ConversationId={ConversationId} Strategy={Strategy}")]
    internal static partial void CompactionRecovered(
        ILogger logger,
        string conversationId,
        string strategy,
        Exception? exception);

    [LoggerMessage(
        EventId = 1451,
        Level = LogLevel.Warning,
        Message = "Conversation compaction audit write failed. ConversationId={ConversationId} Strategy={Strategy}")]
    internal static partial void AuditWriteFailed(
        ILogger logger,
        string conversationId,
        string strategy,
        Exception? exception);
}
