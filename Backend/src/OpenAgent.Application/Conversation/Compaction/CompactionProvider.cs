using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace OpenAgent.Application.Conversation.Compaction;

/// <summary>压缩的索引构建/回写入口：手动压缩与轮次内压缩共用同一条路径。</summary>
internal static class CompactionProvider
{
    public static async Task<IEnumerable<ChatMessage>> CompactAsync(
        CompactionStrategy strategy,
        IEnumerable<ChatMessage> messages,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        CompactionMessageIndex index = CompactionMessageIndex.Build([.. messages]);
        await strategy.CompactAsync(index, logger, cancellationToken).ConfigureAwait(false);
        return index.GetIncludedMessages();
    }
}
