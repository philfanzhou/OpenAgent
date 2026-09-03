using System.Text;
using Microsoft.Extensions.AI;

namespace OpenAgent.Core.Conversation;

/// <summary>
/// Enforces a hard output budget for the dedicated summarization call.
/// Prompt instructions alone are not a reliable token limit.
/// </summary>
internal sealed class OutputTokenLimitedChatClient(
    IChatClient innerClient,
    int maxOutputTokens) : DelegatingChatClient(innerClient)
{
    // Reasoning models may consume the request's generation allowance before
    // emitting visible summary text. This is generation headroom only; the
    // persisted summary is still bounded by MaxOutputTokens below.
    private const int MinimumGenerationTokens = 2_048;

    internal int MaxOutputTokens { get; } = maxOutputTokens > 0
        ? maxOutputTokens
        : throw new ArgumentOutOfRangeException(nameof(maxOutputTokens));

    internal int GenerationTokenLimit => Math.Max(MinimumGenerationTokens, MaxOutputTokens * 4);

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ChatOptions boundedOptions = options?.Clone() ?? new ChatOptions();
        boundedOptions.MaxOutputTokens = boundedOptions.MaxOutputTokens is > 0
            ? Math.Min(boundedOptions.MaxOutputTokens.Value, GenerationTokenLimit)
            : GenerationTokenLimit;
        ChatResponse response = await base.GetResponseAsync(
            messages,
            boundedOptions,
            cancellationToken).ConfigureAwait(false);
        string? summary = response.Text?.Trim();
        if (string.IsNullOrWhiteSpace(summary))
        {
            throw new InvalidOperationException(
                "Summarization model returned no summary text.");
        }

        string limitedSummary = LimitEstimatedTokens(summary, MaxOutputTokens);
        return string.Equals(summary, limitedSummary, StringComparison.Ordinal)
            ? response
            : new ChatResponse(new ChatMessage(ChatRole.Assistant, limitedSummary));
    }

    private static string LimitEstimatedTokens(string text, int maxTokens)
    {
        // MAF estimates tokens as UTF-8 byte count / 4 when the provider does not
        // expose a tokenizer. Apply the same boundary to the persisted summary.
        int maxBytes = checked(maxTokens * 4);
        if (Encoding.UTF8.GetByteCount(text) <= maxBytes)
        {
            return text;
        }

        var result = new StringBuilder(Math.Min(text.Length, maxBytes));
        int byteCount = 0;
        foreach (Rune rune in text.EnumerateRunes())
        {
            if (byteCount + rune.Utf8SequenceLength > maxBytes)
            {
                break;
            }
            result.Append(rune.ToString());
            byteCount += rune.Utf8SequenceLength;
        }
        return result.ToString().TrimEnd();
    }
}
