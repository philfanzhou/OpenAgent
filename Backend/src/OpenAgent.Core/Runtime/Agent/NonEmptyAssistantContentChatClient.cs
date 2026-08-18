using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace OpenAgent.Core.Runtime.Agent;

/// <summary>
/// Removes empty text parts before OpenAI Chat Completions serializes assistant tool calls.
/// Tool-call-only messages are intentionally left without a fabricated text placeholder.
/// </summary>
internal sealed class NonEmptyAssistantContentChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        base.GetResponseAsync(Sanitize(messages), options, cancellationToken);

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
            if (message.Role != ChatRole.Assistant
                || !message.Contents.OfType<FunctionCallContent>().Any())
            {
                yield return message;
                continue;
            }

            ChatMessage sanitized = message.Clone();
            sanitized.Contents = sanitized.Contents
                .Where(content => content is not TextContent text
                    || !string.IsNullOrWhiteSpace(text.Text))
                .ToList();
            yield return sanitized;
        }
    }
}
