using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace OpenAgent.Application.Conversation.Compaction;

/// <summary>
/// 用摘要客户端压缩较旧的轮次组：保留最近 <see cref="MinimumPreservedGroups"/> 个轮次，
/// 其余轮次交给摘要模型，替换为一个带 <see cref="CompactionMessageGroup.SummaryPropertyKey"/>
/// 标记的 summary 组。未达到目标 token 时不再循环——候选用尽后即返回。
/// </summary>
internal sealed class SummarizationCompactionStrategy : CompactionStrategy
{
    private const string DefaultPrompt = """
        Summarize the supplied older conversation messages into compact continuation state for a future assistant.
        Preserve the user's active intent, decisions, constraints, preferences, unresolved questions, confirmed facts,
        relevant identifiers, configuration values, and concise tool outcomes or errors.
        Remove greetings, repetition, filler, superseded details, and raw tool output that is no longer needed.
        Return only the summary.
        """;

    private readonly IChatClient _client;
    private readonly string _prompt;
    private readonly CompactionTrigger? _target;

    internal SummarizationCompactionStrategy(
        IChatClient client,
        CompactionTrigger shouldCompactTrigger,
        int minimumPreservedGroups,
        string? summarizationPrompt = null,
        CompactionTrigger? target = null)
        : base(shouldCompactTrigger, shouldCompactTrigger)
    {
        _client = client;
        MinimumPreservedGroups = minimumPreservedGroups;
        _prompt = string.IsNullOrWhiteSpace(summarizationPrompt)
            ? DefaultPrompt.Trim()
            : summarizationPrompt;
        _target = target;
    }

    internal int MinimumPreservedGroups { get; }
    internal IChatClient ChatClient => _client;
    internal string SummarizationPrompt => _prompt;

    protected override async ValueTask<bool> CompactCoreAsync(
        CompactionMessageIndex index,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        List<CompactionMessageGroup> turnGroups = [.. index.Groups
            .Where(group => !group.IsExcluded && group.Kind == CompactionGroupKind.Turn)];
        List<CompactionMessageGroup> candidates = [.. turnGroups.Take(Math.Max(0, turnGroups.Count - MinimumPreservedGroups))];
        if (candidates.Count == 0)
        {
            return false;
        }

        List<ChatMessage> toSummarize = [.. candidates.SelectMany(group => group.Messages)];
        ChatResponse response = await _client.GetResponseAsync(
            [new ChatMessage(ChatRole.System, _prompt), .. toSummarize],
            cancellationToken: cancellationToken).ConfigureAwait(false);
        string summary = string.IsNullOrWhiteSpace(response.Text)
            ? "[Summary unavailable]"
            : response.Text.Trim();

        ChatMessage summaryMessage = new(ChatRole.Assistant, summary)
        {
            AdditionalProperties = new AdditionalPropertiesDictionary { [CompactionMessageGroup.SummaryPropertyKey] = true }
        };
        CompactionMessageGroup summaryGroup = new() { Kind = CompactionGroupKind.Summary };
        summaryGroup.Add(summaryMessage);

        int insertAt = index.Groups.IndexOf(candidates[0]);
        foreach (CompactionMessageGroup candidate in candidates)
        {
            candidate.IsExcluded = true;
            candidate.ExcludeReason = "summarized";
        }
        index.Groups.Insert(insertAt < 0 ? 0 : insertAt, summaryGroup);
        // 候选组已一次性纳入摘要；目标触发器只用于调用方判定是否需要继续压缩，
        // 本轮没有可再压缩的组时不再发起第二次摘要请求。
        return _target is null || _target(index) || candidates.Count == turnGroups.Count;
    }
}
