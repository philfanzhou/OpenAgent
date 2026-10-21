using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace OpenAgent.Application.Conversation.Compaction;

/// <summary>平台自有的压缩索引模型，替代已移除的 MAF Compaction 类型组。</summary>
internal enum CompactionGroupKind
{
    Turn,
    System,
    Summary,
}

internal sealed class CompactionMessageGroup
{
    internal const string SummaryPropertyKey = "openagent:compaction-summary";

    internal CompactionGroupKind Kind { get; init; }
    internal List<ChatMessage> Messages { get; } = [];
    internal int TokenCount { get; private set; }
    internal bool IsExcluded { get; set; }
    internal string? ExcludeReason { get; set; }

    internal void Add(ChatMessage message)
    {
        Messages.Add(message);
        TokenCount += EstimateTokens(message);
    }

    internal static int EstimateTokens(ChatMessage message) =>
        Math.Max(1, (int)Math.Ceiling(Encoding.UTF8.GetByteCount(message.Text ?? string.Empty) / 4d));
}

/// <summary>
/// 把消息序列分组为可压缩的轮次：system 消息各自成组且不参与压缩统计；
/// 每条 user 消息开启新组，后续 assistant/tool 消息并入当前组。
/// </summary>
internal sealed class CompactionMessageIndex
{
    internal List<CompactionMessageGroup> Groups { get; } = [];

    internal int IncludedNonSystemGroupCount => Groups.Count(group =>
        !group.IsExcluded && group.Kind != CompactionGroupKind.System);

    internal List<ChatMessage> GetIncludedMessages() =>
        [.. Groups.Where(group => !group.IsExcluded).SelectMany(group => group.Messages)];

    internal int IncludedTokenCount => Groups
        .Where(group => !group.IsExcluded)
        .Sum(group => group.TokenCount);

    internal static CompactionMessageIndex Build(IReadOnlyList<ChatMessage> messages)
    {
        CompactionMessageIndex index = new();
        CompactionMessageGroup? current = null;
        foreach (ChatMessage message in messages)
        {
            if (message.Role == ChatRole.System)
            {
                CompactionMessageGroup system = new() { Kind = CompactionGroupKind.System };
                system.Add(message);
                index.Groups.Add(system);
                current = null;
                continue;
            }
            if (current is null || message.Role == ChatRole.User)
            {
                current = new CompactionMessageGroup { Kind = CompactionGroupKind.Turn };
                index.Groups.Add(current);
            }
            current.Add(message);
        }
        return index;
    }
}

internal delegate bool CompactionTrigger(CompactionMessageIndex index);

internal static class CompactionTriggers
{
    internal static readonly CompactionTrigger Always = _ => true;

    internal static CompactionTrigger TokensExceed(int tokens) =>
        index => index.IncludedTokenCount > tokens;

    internal static CompactionTrigger TokensBelow(int tokens) =>
        index => index.IncludedTokenCount < tokens;

    internal static CompactionTrigger MessagesExceed(int count) =>
        index => index.GetIncludedMessages().Count > count;
}

internal abstract class CompactionStrategy(CompactionTrigger invokingTrigger, CompactionTrigger invokedTrigger)
{
    protected CompactionTrigger InvokingTrigger { get; } = invokingTrigger;
    protected CompactionTrigger InvokedTrigger { get; } = invokedTrigger;

    public async Task<bool> CompactAsync(CompactionMessageIndex index, ILogger logger, CancellationToken cancellationToken)
    {
        if (!InvokingTrigger(index))
        {
            return false;
        }
        return await CompactCoreAsync(index, logger, cancellationToken).ConfigureAwait(false);
    }

    protected abstract ValueTask<bool> CompactCoreAsync(
        CompactionMessageIndex index,
        ILogger logger,
        CancellationToken cancellationToken);
}
