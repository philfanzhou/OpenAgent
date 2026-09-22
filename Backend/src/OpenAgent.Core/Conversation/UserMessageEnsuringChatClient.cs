using Microsoft.Extensions.AI;

namespace OpenAgent.Core.Conversation;

/// <summary>
/// 确保发往模型的请求至少含一条 user 消息：部分服务端模板（如 Qwen 系 chat 模板）
/// 会拒绝没有 user 消息的请求，而 MAF 的摘要压缩调用只发系统/助手侧指令。
/// 没有任何 user 消息时，在末尾补一条最小占位（不改既有消息语义）。
/// </summary>
internal sealed class UserMessageEnsuringChatClient(IChatClient innerClient)
    : DelegatingChatClient(innerClient)
{
    private const string Placeholder = "Please summarize the conversation above following the requirements.";

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        base.GetResponseAsync(EnsureUserMessage(messages), options, cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        base.GetStreamingResponseAsync(EnsureUserMessage(messages), options, cancellationToken);

    internal static IReadOnlyList<ChatMessage> EnsureUserMessage(IEnumerable<ChatMessage> messages)
    {
        List<ChatMessage> list = [.. messages];
        if (list.Any(message => message.Role == ChatRole.User))
        {
            return list;
        }
        list.Add(new ChatMessage(ChatRole.User, Placeholder));
        return list;
    }
}
