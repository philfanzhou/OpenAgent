using System.Diagnostics;
using OpenAgent.Contracts.Engine;
using OpenAgent.Core.Observability;
using Xunit;

namespace OpenAgent.Core.Tests.Observability;

public class AgentActivityTagsTests
{
    [Fact]
    public void Helpers_SetExpectedActivityTags()
    {
        using var activity = new Activity("agent-test").Start();

        AgentActivityTags.SetRequest("agent-1", "tenant-1", "conversation-1", "chat");
        AgentActivityTags.SetTokenUsage(new TokenUsage { PromptTokens = 10, CompletionTokens = 20, TotalTokens = 30 });
        AgentActivityTags.SetOpenAiRetry(2, "timeout");

        var tags = activity.TagObjects.ToDictionary(tag => tag.Key, tag => tag.Value);
        Assert.Equal("agent-1", tags["agent.id"]);
        Assert.Equal("tenant-1", tags["tenant.id"]);
        Assert.Equal("conversation-1", tags["conversation.id"]);
        Assert.Equal("chat", tags["intent"]);
        Assert.Equal(10, tags["tokens.prompt"]);
        Assert.Equal(20, tags["tokens.completion"]);
        Assert.Equal(30, tags["tokens.total"]);
        Assert.Equal(2, tags["openai.retry_count"]);
        Assert.Equal("timeout", tags["openai.retry_reason"]);
    }
}
