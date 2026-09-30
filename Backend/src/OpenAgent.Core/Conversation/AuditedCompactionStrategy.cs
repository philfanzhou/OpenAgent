using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Text;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Core.Runtime.Agent;

namespace OpenAgent.Core.Conversation;

/// <summary>
/// Adds durable audit metadata around an existing MAF compaction strategy.
/// The wrapped strategy remains the only component that changes model context.
/// </summary>
internal sealed class AuditedCompactionStrategy : CompactionStrategy
{
    private const string StrategyName = "summarization";
    private const double MinimumTokenSavingsRatio = 0.1;
    private readonly CompactionStrategy _strategy;
    private readonly CompactionTrigger _triggerCondition;
    private readonly string _trigger;
    private readonly string? _tenantId;
    private readonly string? _conversationId;
    private readonly IConversationStore _store;
    private readonly ILogger<AuditedCompactionStrategy> _logger;
    private readonly bool _recordUnchanged;
    private int _lastAttemptBoundary = -1;
    private int _originalStartSequence;
    private int _originalEndSequence;

    internal ContextSummary? LastAudit { get; private set; }
    internal bool LastAuditRecorded { get; private set; }

    internal Task RecordNotRunAsync(IList<ChatMessage> messages)
    {
        int tokenCount = messages.Sum(message =>
            Math.Max(1, (int)Math.Ceiling(Encoding.UTF8.GetByteCount(message.Text ?? string.Empty) / 4d)));
        return RecordSkippedAsync(
            "The conversation does not contain a completed message group that MAF can compact.",
            tokenCount,
            originalHistoryRestored: false,
            sourceEndSequence: Math.Max(messages.Count, messages.Select(CompactionMessageMetadata.SourceSequence).DefaultIfEmpty().Max()));
    }

    internal AuditedCompactionStrategy(
        CompactionStrategy strategy,
        CompactionTrigger triggerCondition,
        string trigger,
        string? tenantId,
        string? conversationId,
        IConversationStore store,
        ILogger<AuditedCompactionStrategy> logger,
        bool recordUnchanged)
        : base(CompactionTriggers.Always, CompactionTriggers.Always)
    {
        _strategy = strategy;
        _triggerCondition = triggerCondition;
        _trigger = trigger;
        _tenantId = tenantId;
        _conversationId = conversationId;
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
        int sourceEndSequence = CompactionMessageMetadata.StampNewMessages(index);
        _originalStartSequence = 0;
        _originalEndSequence = 0;
        int originalTokenCount = GetIncludedTokenCount(index);
        bool triggerFired = _triggerCondition(index);
        bool canCompact = CanCompact(index);
        if (!_recordUnchanged && _lastAttemptBoundary == sourceEndSequence) return false;
        if (!triggerFired || !canCompact)
        {
            if (_recordUnchanged)
            {
                string reason = !triggerFired
                    ? "Context is already within the compaction target budget."
                    : "There is not enough older context to compact while preserving recent messages.";
                await RecordSkippedAsync(reason, originalTokenCount,
                    originalHistoryRestored: false, sourceEndSequence).ConfigureAwait(false);
            }
            return false;
        }

        try
        {
            _lastAttemptBoundary = sourceEndSequence;
            bool compacted = await _strategy.CompactAsync(
                index,
                logger,
                cancellationToken).ConfigureAwait(false);
            List<ChatMessage> after = index.GetIncludedMessages().ToList();
            int compressedMessageCount = CountRemoved(before, after);
            if (!compacted)
            {
                Restore(index, originalGroups);
                await RecordFailureAsync(
                    "Compaction strategy did not produce a compacted context.",
                    originalTokenCount,
                    GetIncludedTokenCount(index),
                    sourceEndSequence).ConfigureAwait(false);
                ConversationCompactionLog.CompactionRecovered(
                    _logger,
                    _conversationId ?? string.Empty,
                    exception: null);
                return false;
            }

            int compactedTokenCount = GetIncludedTokenCount(index);
            string? generatedSummary = ReadSummary(index);
            if (string.IsNullOrWhiteSpace(generatedSummary)
                || generatedSummary.Contains(
                    "[Summary unavailable]",
                    StringComparison.OrdinalIgnoreCase))
            {
                Restore(index, originalGroups);
                await RecordFailureAsync(
                    "Summarization model did not return usable summary text.",
                    originalTokenCount,
                    originalTokenCount,
                    sourceEndSequence).ConfigureAwait(false);
                return false;
            }

            int tokenSavings = originalTokenCount - compactedTokenCount;
            int minimumSavings = Math.Max(
                1,
                (int)Math.Ceiling(originalTokenCount * MinimumTokenSavingsRatio));
            if (tokenSavings < minimumSavings)
            {
                Restore(index, originalGroups);
                await RecordSkippedAsync(
                    $"Generated context was rejected because it saved {Math.Max(0, tokenSavings)} tokens; "
                    + $"at least {minimumSavings} tokens (10%) are required.",
                    originalTokenCount,
                    originalHistoryRestored: true,
                    sourceEndSequence).ConfigureAwait(false);
                ConversationCompactionLog.CompactionRejected(
                    _logger,
                    _conversationId ?? string.Empty,
                    originalTokenCount,
                    compactedTokenCount);
                return false;
            }

            string summary = generatedSummary;
            List<ChatMessage> removed = before.Where(message => !after.Contains(message)).ToList();
            _originalStartSequence = removed.Min(message =>
            {
                int sequence = 1;
                bool wasSummary = message.AdditionalProperties?.TryGetValue(CompactionMessageGroup.SummaryPropertyKey, out object? value) == true && value is true;
                return wasSummary ? 1 : Math.Max(1, CompactionMessageMetadata.SourceSequence(message)
                    - AgentMessageAdapter.ToStored([message], ref sequence).Count() + 1);
            });
            _originalEndSequence = removed.Select(CompactionMessageMetadata.SourceSequence).Max();
            foreach (CompactionMessageGroup group in index.Groups.Where(group =>
                !group.IsExcluded && group.Kind == CompactionGroupKind.Summary
                && !originalGroups.Any(snapshot => ReferenceEquals(snapshot.Group, group))))
            {
                foreach (ChatMessage message in group.Messages)
                    (message.AdditionalProperties ??= [])[CompactionMessageMetadata.SourceSequenceKey] = _originalEndSequence;
            }
            await TryRecordAsync(
                status: "Succeeded",
                summary,
                result: summary,
                error: null,
                compressedMessageCount: compressedMessageCount,
                originalTokenCount: originalTokenCount,
                tokenCount: compactedTokenCount,
                originalHistoryRestored: false,
                compactedMessages: after,
                sourceEndSequence: sourceEndSequence,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!LastAuditRecorded)
            {
                // A transient projection must never advance past the durable one.
                Restore(index, originalGroups);
                return false;
            }
            return true;
        }
        catch (OperationCanceledException)
        {
            Restore(index, originalGroups);
            throw;
        }
        catch (Exception exception)
        {
            Restore(index, originalGroups);
            await RecordFailureAsync(exception.Message, originalTokenCount, GetIncludedTokenCount(index), sourceEndSequence).ConfigureAwait(false);
            ConversationCompactionLog.CompactionRecovered(
                _logger,
                _conversationId ?? string.Empty,
                exception);
            return false;
        }
    }

    private Task RecordFailureAsync(
        string error,
        int originalTokenCount,
        int tokenCount,
        int sourceEndSequence = 0) => TryRecordAsync(
        status: "Failed",
        summary: null,
        result: "Original history restored for model invocation.",
        error,
        compressedMessageCount: 0,
        originalTokenCount: originalTokenCount,
        tokenCount: tokenCount,
        originalHistoryRestored: true,
        compactedMessages: null,
        sourceEndSequence: sourceEndSequence,
        cancellationToken: CancellationToken.None);

    private Task RecordSkippedAsync(
        string result,
        int tokenCount,
        bool originalHistoryRestored,
        int sourceEndSequence = 0) => TryRecordAsync(
            status: "Skipped",
            summary: null,
            result,
            error: null,
            compressedMessageCount: 0,
            originalTokenCount: tokenCount,
            tokenCount: tokenCount,
            originalHistoryRestored,
            compactedMessages: null,
            sourceEndSequence: sourceEndSequence,
            cancellationToken: CancellationToken.None);

    private async Task TryRecordAsync(
        string status,
        string? summary,
        string? result,
        string? error,
        int compressedMessageCount,
        int originalTokenCount,
        int tokenCount,
        bool originalHistoryRestored,
        IReadOnlyList<ChatMessage>? compactedMessages,
        int sourceEndSequence = 0,
        CancellationToken cancellationToken = default)
    {
        LastAudit = null;
        LastAuditRecorded = false;
        if (string.IsNullOrWhiteSpace(_tenantId)
            || string.IsNullOrWhiteSpace(_conversationId))
        {
            return;
        }

        try
        {
            ConversationRecord? conversation = await _store.GetRecordAsync(
                _tenantId,
                _conversationId,
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
                Strategy = StrategyName,
                Trigger = _trigger,
                Status = status,
                Summary = summary,
                Result = result,
                Error = error,
                LastCompressedAt = DateTimeOffset.UtcNow,
                CompressedMessageCount = compressedMessageCount,
                OriginalStartSequence = _originalStartSequence > 0 ? _originalStartSequence : rangeCount > 0 ? 1 : 0,
                OriginalEndSequence = _originalEndSequence > 0 ? _originalEndSequence : rangeCount,
                OriginalTokenCount = originalTokenCount,
                TokenCount = tokenCount,
                OriginalHistoryRestored = originalHistoryRestored,
                // Raw storage positions include messages produced during this run,
                // even before the final append commits them.
                SourceEndSequence = sourceEndSequence,
                ProjectionVersion = 1,
                CompactedMessages = ToStored(compactedMessages)
            };
            LastAudit = audit;
            bool recorded = await _store.RecordCompressionAsync(
                _tenantId,
                _conversationId,
                audit,
                cancellationToken).ConfigureAwait(false);
            LastAuditRecorded = recorded;
            if (!recorded)
            {
                ConversationCompactionLog.AuditWriteFailed(
                    _logger,
                    _conversationId,
                    exception: null);
            }
        }
        catch (Exception exception)
        {
            ConversationCompactionLog.AuditWriteFailed(
                _logger,
                _conversationId,
                exception);
        }
    }

    private static List<ConversationMessage> ToStored(IReadOnlyList<ChatMessage>? messages)
    {
        if (messages == null)
        {
            return [];
        }

        List<ConversationMessage> result = [];
        int sequence = 1;
        foreach (ChatMessage message in messages)
        {
            IReadOnlyList<string> fileIds = message.AdditionalProperties?.TryGetValue(
                CompactionMessageMetadata.FileIdsKey, out object? ids) == true
                && ids is IReadOnlyList<string> files ? files : [];
            int source = CompactionMessageMetadata.SourceSequence(message);
            result.AddRange(AgentMessageAdapter.ToStored([message], ref sequence)
                .Select(row => row with { Sequence = source > 0 ? source : row.Sequence, FileIds = fileIds }));
        }
        return result;
    }

    private static int CountRemoved(
        IReadOnlyList<ChatMessage> before,
        IReadOnlyList<ChatMessage> after)
    {
        var retained = new HashSet<ChatMessage>(after, ReferenceComparer.Instance);
        return before.Where(message => !retained.Contains(message)).Sum(message =>
        {
            int sequence = 1;
            return AgentMessageAdapter.ToStored([message], ref sequence).Count();
        });
    }

    private static int GetIncludedTokenCount(CompactionMessageIndex index) => index.Groups
        .Where(group => !group.IsExcluded)
        .Sum(group => group.TokenCount);

    private bool CanCompact(CompactionMessageIndex index) => _strategy switch
    {
        TurnCompactionStrategy turns => turns.CanCompact(index),
        SummarizationCompactionStrategy summary => index.IncludedNonSystemGroupCount > summary.MinimumPreservedGroups,
        _ => index.IncludedNonSystemGroupCount > 1
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
        Message = "Conversation summarization failed and original history was restored. ConversationId={ConversationId}")]
    internal static partial void CompactionRecovered(
        ILogger logger,
        string conversationId,
        Exception? exception);

    [LoggerMessage(
        EventId = 1451,
        Level = LogLevel.Warning,
        Message = "Conversation summarization audit write failed. ConversationId={ConversationId}")]
    internal static partial void AuditWriteFailed(
        ILogger logger,
        string conversationId,
        Exception? exception);

    [LoggerMessage(
        EventId = 1452,
        Level = LogLevel.Warning,
        Message = "Conversation summary was rejected because token savings were insufficient. ConversationId={ConversationId} OriginalTokens={OriginalTokens} GeneratedTokens={GeneratedTokens}")]
    internal static partial void CompactionRejected(
        ILogger logger,
        string conversationId,
        int originalTokens,
        int generatedTokens);
}
