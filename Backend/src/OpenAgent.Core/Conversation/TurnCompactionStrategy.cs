using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAgent.Core.Runtime.Agent;

namespace OpenAgent.Core.Conversation;

/// <summary>Uses MAF's atomic groups while retaining user turns, including the active query.</summary>
internal sealed class TurnCompactionStrategy(
    IChatClient chatClient,
    int preserveRecentTurns,
    string summarizationPrompt,
    int targetTokens,
    int contextTokens = 128_000) : CompactionStrategy(CompactionTriggers.Always, CompactionTriggers.Always)
{
    internal IChatClient ChatClient { get; } = chatClient;
    internal string SummarizationPrompt { get; } = summarizationPrompt;
    internal int PreserveRecentTurns { get; } = Math.Max(1, preserveRecentTurns);
    internal int RequestOverheadTokens { get; set; }

    internal bool CanCompact(CompactionMessageIndex index) => SelectGroups(index).Count > 0;

    private List<CompactionMessageGroup> SelectGroups(CompactionMessageIndex index)
    {
        List<CompactionMessageGroup> groups = index.Groups
            .Where(group => !group.IsExcluded && group.Kind != CompactionGroupKind.System).ToList();
        List<int> users = groups.Select((group, position) => (group, position))
            .Where(item => item.group.Kind == CompactionGroupKind.User)
            .Select(item => item.position).ToList();
        if (users.Count == 0) return [];

        // PreserveRecentTurns refers to user turns, not SDK message groups.
        int boundary = users[Math.Max(0, users.Count - PreserveRecentTurns)];
        List<CompactionMessageGroup> candidates = groups.Take(boundary).ToList();
        int summaryBudget = ChatClient is OutputTokenLimitedChatClient limited ? limited.MaxOutputTokens : 512;
        bool OverTarget(List<CompactionMessageGroup> selected) => targetTokens > 0
            && index.IncludedTokenCount - selected.Sum(group => group.TokenCount)
                + RequestOverheadTokens + summaryBudget > targetTokens;
        if (OverTarget(candidates))
        {
            // Recent-turn retention is a preference; the current user query is
            // the hard floor. Large preceding turns must not defeat auto compaction.
            candidates = groups.Take(users[^1]).ToList();
        }
        if (candidates.Count == 0 && groups.Skip(users[^1] + 1).Any(group => group.Kind == CompactionGroupKind.ToolCall))
        {
            // Long tool loops can fill a window within one user turn. Preserve every
            // user message and the newest two atomic groups; summarize older results.
            candidates = groups.Take(Math.Max(0, groups.Count - 2))
                .Where(group => group.Kind != CompactionGroupKind.User).ToList();
        }
        if (OverTarget(candidates) && groups.Skip(users[^1] + 1).Any(group => group.Kind == CompactionGroupKind.ToolCall))
        {
            // Keep the latest complete tool group and current query when even
            // two recent tool groups exceed the target.
            candidates = groups.Take(Math.Max(0, groups.Count - 1))
                .Where(group => !ReferenceEquals(group, groups[users[^1]])).ToList();
        }
        return candidates;
    }

    protected override async ValueTask<bool> CompactCoreAsync(
        CompactionMessageIndex index, ILogger logger, CancellationToken cancellationToken)
    {
        List<CompactionMessageGroup> candidates = SelectGroups(index);
        if (candidates.Count == 0) return false;
        int remaining = index.IncludedTokenCount;
        List<CompactionMessageGroup> selected = [];
        foreach (CompactionMessageGroup group in candidates)
        {
            selected.Add(group);
            remaining -= group.TokenCount;
            int summaryBudget = ChatClient is OutputTokenLimitedChatClient limited ? limited.MaxOutputTokens : 512;
            if (targetTokens > 0 && remaining + RequestOverheadTokens + summaryBudget <= targetTokens) break;
        }

        // A user transcript keeps strict provider templates valid even when the
        // selected groups contain only old summaries or tool results. Tool data is
        // quoted context rather than executable calls in this dedicated request.
        int sequence = 1;
        string transcript = JsonSerializer.Serialize(AgentMessageAdapter.ToStored(
            selected.SelectMany(group => group.Messages), ref sequence)
            .Select(message => new { message.Role, message.Content, message.ToolName,
                message.ToolCallId, Arguments = message.Metadata?.ToolArguments, message.FileIds }),
            new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        string summary = await SummarizeAsync(transcript, cancellationToken).ConfigureAwait(false);

        int insertIndex = index.Groups.IndexOf(selected[0]);
        foreach (CompactionMessageGroup group in selected)
        {
            group.IsExcluded = true;
            group.ExcludeReason = "Summarized conversation context";
        }
        ChatMessage summaryMessage = new(ChatRole.Assistant, $"[Conversation summary]\n{summary}");
        (summaryMessage.AdditionalProperties ??= [])[CompactionMessageGroup.SummaryPropertyKey] = true;
        index.InsertGroup(insertIndex, CompactionGroupKind.Summary, [summaryMessage]);
        return true;
    }

    private async Task<string> SummarizeAsync(string transcript, CancellationToken cancellationToken)
    {
        int generation = ChatClient is OutputTokenLimitedChatClient limited ? limited.GenerationTokenLimit : 2_048;
        int promptTokens = (int)Math.Ceiling(Encoding.UTF8.GetByteCount(SummarizationPrompt) / 4d);
        int inputBytes = Math.Max(4, (contextTokens - generation - promptTokens - 128) * 4);
        string state = string.Empty;
        int offset = 0;
        while (offset < transcript.Length)
        {
            string prefix = state.Length == 0 ? string.Empty : $"Continuation state from previous transcript chunks:\n{state}\nMerge with the next transcript chunk:\n";
            int chunkBudget = inputBytes - Encoding.UTF8.GetByteCount(prefix);
            if (chunkBudget < 4) throw new InvalidOperationException("Model context window is too small for compaction instructions and summary output.");
            int end = offset;
            int bytes = 0;
            while (end < transcript.Length)
            {
                Rune rune = Rune.GetRuneAt(transcript, end);
                if (bytes + rune.Utf8SequenceLength > chunkBudget) break;
                bytes += rune.Utf8SequenceLength;
                end += rune.Utf16SequenceLength;
            }
            ChatResponse response = await ChatClient.GetResponseAsync(
                [new ChatMessage(ChatRole.System, SummarizationPrompt),
                 new ChatMessage(ChatRole.User, prefix + transcript[offset..end])],
                cancellationToken: cancellationToken).ConfigureAwait(false);
            state = response.Text.Trim();
            if (string.IsNullOrWhiteSpace(state) || state.Contains("[Summary unavailable]", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Summarization model returned no usable summary text.");
            offset = end;
        }
        return state;
    }
}
