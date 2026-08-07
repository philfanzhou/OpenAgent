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
        if (usage == null)
        {
            return null;
        }

        return new TokenUsage
        {
            PromptTokens = Convert.ToInt32(usage.InputTokenCount.GetValueOrDefault()),
            CompletionTokens = Convert.ToInt32(usage.OutputTokenCount.GetValueOrDefault()),
            TotalTokens = Convert.ToInt32(usage.TotalTokenCount.GetValueOrDefault())
        };
    }
}
