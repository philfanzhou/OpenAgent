using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;

namespace OpenAgent.Core.Conversation;

/// <summary>Accounts for instructions and tools before the inner per-call compaction provider.</summary>
internal sealed class CompactionBudgetChatClient(IChatClient inner, TurnCompactionStrategy strategy)
    : DelegatingChatClient(inner)
{
    private void SetBudget(ChatOptions? options)
    {
        int bytes = Encoding.UTF8.GetByteCount(options?.Instructions ?? string.Empty);
        foreach (AITool tool in options?.Tools ?? [])
        {
            bytes += Encoding.UTF8.GetByteCount(tool.Name + tool.Description);
            if (tool is AIFunction function) bytes += Encoding.UTF8.GetByteCount(function.JsonSchema.GetRawText());
        }
        strategy.RequestOverheadTokens = (int)Math.Ceiling(bytes / 4d);
    }

    public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        SetBudget(options);
        return base.GetResponseAsync(messages, options, cancellationToken);
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        SetBudget(options);
        await foreach (ChatResponseUpdate update in base.GetStreamingResponseAsync(messages, options, cancellationToken)
            .ConfigureAwait(false)) yield return update;
    }
}
