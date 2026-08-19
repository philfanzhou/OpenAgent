using Microsoft.Extensions.AI;
using OpenAgent.Contracts.Requests;

namespace OpenAgent.Core.Runtime.Agent;

internal static class AgentResponseAdapter
{
    internal static TokenUsage? ReadUsage(IEnumerable<AIContent> contents)
    {
        UsageContent? usage = contents.OfType<UsageContent>().LastOrDefault();
        return ConvertUsage(usage?.Details);
    }

    internal static TokenUsage? ConvertUsage(UsageDetails? usage)
    {
        if (usage?.InputTokenCount == null
            || usage.OutputTokenCount == null
            || usage.TotalTokenCount == null)
        {
            return null;
        }

        int? promptTokens = ConvertCount(usage.InputTokenCount.Value);
        int? completionTokens = ConvertCount(usage.OutputTokenCount.Value);
        int? totalTokens = ConvertCount(usage.TotalTokenCount.Value);
        if (promptTokens == null || completionTokens == null || totalTokens == null)
        {
            return null;
        }

        return new TokenUsage
        {
            PromptTokens = promptTokens.Value,
            CompletionTokens = completionTokens.Value,
            TotalTokens = totalTokens.Value,
            CachedInputTokens = ConvertCount(usage.CachedInputTokenCount),
            ReasoningTokens = ConvertCount(usage.ReasoningTokenCount)
        };
    }

    internal static string ReadModelId(object? rawRepresentation, string configuredModelId)
    {
        string? providerModelId = rawRepresentation switch
        {
            Microsoft.Extensions.AI.ChatResponse response => response.ModelId,
            ChatResponseUpdate update => update.ModelId,
            _ => null
        };
        return string.IsNullOrWhiteSpace(providerModelId)
            ? configuredModelId
            : providerModelId;
    }

    private static int? ConvertCount(long? count)
    {
        return count is >= 0 and <= int.MaxValue
            ? (int)count.Value
            : null;
    }
}
