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
    private readonly SummarizationCompactionStrategy _strategy;
    private readonly CompactionTrigger _triggerCondition;
    private readonly string _trigger;
    private readonly string? _tenantId;
    private readonly string? _conversationId;
    private readonly IConversationStore _store;
    private readonly ILogger<AuditedCompactionStrategy> _logger;
    private readonly bool _recordUnchanged;

    internal ContextSummary? LastAudit { get; private set; }
    internal bool LastAuditRecorded { get; private set; }

    internal Task RecordNotRunAsync(IList<ChatMessage> messages)
    {
        int tokenCount = messages.Sum(message =>
            Math.Max(1, (int)Math.Ceiling(Encoding.UTF8.GetByteCount(message.Text ?? string.Empty) / 4d)));
        return RecordSkippedAsync(
            "The conversation does not contain a completed message group that MAF can compact.",
            tokenCount,
            originalHistoryRestored: false);
    }

    internal AuditedCompactionStrategy(
        SummarizationCompactionStrategy strategy,
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
        int originalTokenCount = GetIncludedTokenCount(index);
        bool triggerFired = _triggerCondition(index);
        bool canCompact = CanCompact(index);
        if (!triggerFired || !canCompact)
        {
            if (_recordUnchanged)
            {
                string reason = !triggerFired
                    ? "Context is already within the compaction target budget."
                    : "There is not enough older context to compact while preserving recent messages.";
                await RecordSkippedAsync(
                    reason,
                    originalTokenCount,
                    originalHistoryRestored: false).ConfigureAwait(false);
            }
            return false;
        }

        try
        {
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
                    GetIncludedTokenCount(index)).ConfigureAwait(false);
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
                    originalTokenCount).ConfigureAwait(false);
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
                    originalHistoryRestored: true).ConfigureAwait(false);
                ConversationCompactionLog.CompactionRejected(
                    _logger,
                    _conversationId ?? string.Empty,
                    originalTokenCount,
                    compactedTokenCount);
                return false;
            }

            string summary = generatedSummary;
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
                cancellationToken: cancellationToken).ConfigureAwait(false);
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
            await RecordFailureAsync(exception.Message, originalTokenCount, GetIncludedTokenCount(index)).ConfigureAwait(false);
            ConversationCompactionLog.CompactionRecovered(
                _logger,
                _conversationId ?? string.Empty,
                exception);
            return false;
        }
    }

    private Task RecordFailureAsync(string error, int originalTokenCount, int tokenCount) => TryRecordAsync(
        status: "Failed",
        summary: null,
        result: "Original history restored for model invocation.",
        error,
        compressedMessageCount: 0,
        originalTokenCount: originalTokenCount,
        tokenCount: tokenCount,
        originalHistoryRestored: true,
        compactedMessages: null,
        cancellationToken: CancellationToken.None);

    private Task RecordSkippedAsync(
        string result,
        int tokenCount,
        bool originalHistoryRestored) => TryRecordAsync(
            status: "Skipped",
            summary: null,
            result,
            error: null,
            compressedMessageCount: 0,
            originalTokenCount: tokenCount,
            tokenCount: tokenCount,
            originalHistoryRestored,
            compactedMessages: null,
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
        CancellationToken cancellationToken)
    {
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
                OriginalStartSequence = rangeCount > 0 ? 1 : 0,
                OriginalEndSequence = rangeCount,
                OriginalTokenCount = originalTokenCount,
                TokenCount = tokenCount,
                OriginalHistoryRestored = originalHistoryRestored,
                SourceEndSequence = conversation?.MessageCount ?? 0,
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

    private static int GetIncludedTokenCount(CompactionMessageIndex index) => index.Groups
        .Where(group => !group.IsExcluded)
        .Sum(group => group.TokenCount);

    private bool CanCompact(CompactionMessageIndex index) =>
        index.IncludedNonSystemGroupCount > _strategy.MinimumPreservedGroups;

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
