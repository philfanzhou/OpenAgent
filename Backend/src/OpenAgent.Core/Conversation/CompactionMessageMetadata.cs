using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using OpenAgent.Core.Runtime.Agent;

namespace OpenAgent.Core.Conversation;

/// <summary>Tracks raw storage positions across SDK grouping and repeated summaries.</summary>
internal static class CompactionMessageMetadata
{
    internal const string SourceSequenceKey = "openagent.sourceSequence";
    internal const string FileIdsKey = "openagent.fileIds";

    internal static int SourceSequence(ChatMessage message) =>
        message.AdditionalProperties?.TryGetValue(SourceSequenceKey, out object? value) == true
            && value is int sequence ? sequence : 0;

    internal static int StampNewMessages(CompactionMessageIndex index)
    {
        List<ChatMessage> messages = index.Groups.SelectMany(group => group.Messages).ToList();
        int end = messages.Select(SourceSequence).DefaultIfEmpty().Max();
        foreach (ChatMessage message in index.Groups.Where(group => group.Kind != CompactionGroupKind.System
                && group.Kind != CompactionGroupKind.Summary).SelectMany(group => group.Messages))
        {
            if (SourceSequence(message) > 0) continue;
            int start = 1;
            end += AgentMessageAdapter.ToStored([message], ref start).Count();
            (message.AdditionalProperties ??= [])[SourceSequenceKey] = end;
        }
        return end;
    }

    internal static void Copy(ChatMessage source, ChatMessage target)
    {
        if (source.AdditionalProperties is null) return;
        target.AdditionalProperties ??= [];
        foreach (var property in source.AdditionalProperties)
        {
            if (property.Key == FileIdsKey && property.Value is IReadOnlyList<string> sourceIds
                && target.AdditionalProperties.TryGetValue(FileIdsKey, out object? ids) && ids is IReadOnlyList<string> targetIds)
                target.AdditionalProperties[FileIdsKey] = targetIds.Concat(sourceIds).Distinct(StringComparer.Ordinal).ToList();
            else target.AdditionalProperties[property.Key] = property.Value;
        }
    }
}
