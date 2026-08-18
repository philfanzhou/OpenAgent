using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace OpenAgent.Core.Runtime.Agent;

/// <summary>
/// 发往模型前，确保 assistant 工具调用消息不携带空文本 content 分片。
/// reasoning 模型（如 kimi）常在调用工具前流式输出一个空的 content 分片（content:""），
/// MEAI 会原样保留为 <see cref="TextContent"/>("")，序列化后变成
/// content: [{"type":"text","text":""}]，部分 OpenAI 兼容供应商（如 moonshot）会以
/// "text content is empty" 拒绝该请求。这里在转发前移除这些空文本分片；
/// 若消息完全没有正文（仅推理 + 工具调用，reasoning 不会被序列化），
/// 则补一个回显工具名的非空占位，不影响模型对工具调用的理解。
/// </summary>
internal sealed class NonEmptyMessageChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => base.GetResponseAsync(Sanitize(messages), options, cancellationToken);

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (ChatResponseUpdate update in base
            .GetStreamingResponseAsync(Sanitize(messages), options, cancellationToken)
            .ConfigureAwait(false))
        {
            yield return update;
        }
    }

    private static IEnumerable<ChatMessage> Sanitize(IEnumerable<ChatMessage> messages)
    {
        foreach (ChatMessage message in messages)
        {
            if (message.Role == ChatRole.Assistant
                && message.Contents.OfType<FunctionCallContent>().Any())
            {
                // 移除模型发出的空文本 content 分片（reasoning 模型常在工具调用前发 content:""）。
                List<TextContent> emptyTexts = message.Contents
                    .OfType<TextContent>()
                    .Where(text => string.IsNullOrWhiteSpace(text.Text))
                    .ToList();
                foreach (TextContent empty in emptyTexts)
                {
                    message.Contents.Remove(empty);
                }

                if (string.IsNullOrWhiteSpace(message.Text))
                {
                    string placeholder = "[Calling: "
                        + string.Join(", ", message.Contents
                            .OfType<FunctionCallContent>()
                            .Select(call => call.Name)
                            .Where(name => !string.IsNullOrWhiteSpace(name)))
                        + "]";
                    message.Contents.Add(new TextContent(placeholder));
                }
            }

            yield return message;
        }
    }
}
