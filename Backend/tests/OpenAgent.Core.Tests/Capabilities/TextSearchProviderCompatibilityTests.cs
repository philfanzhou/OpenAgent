using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace OpenAgent.Core.Tests.Capabilities;

public class TextSearchProviderCompatibilityTests
{
    [Fact]
    public async Task OnDemandProvider_UsesUserQuestionWithoutPerCallLimit()
    {
        string? receivedQuery = null;
        var search = new TextSearchProvider((query, _) =>
        {
            receivedQuery = query;
            return Task.FromResult<IEnumerable<TextSearchProvider.TextSearchResult>>(
                [new() { SourceName = "Policy", Text = "Approved process" }]);
        }, new TextSearchProviderOptions
        {
            SearchTime = TextSearchProviderOptions.TextSearchBehavior.OnDemandFunctionCalling,
            FunctionToolName = "search_knowledge_base"
        });
        using var client = new SearchClient();
        var agent = new ChatClientAgent(client, new ChatClientAgentOptions { AIContextProviders = [search] });
        await foreach (AgentResponseUpdate _ in agent.RunStreamingAsync("Find the policy.")) { }

        Assert.Equal("policy", receivedQuery);
        AIFunction function = Assert.IsAssignableFrom<AIFunction>(client.SearchTool);
        var properties = function.JsonSchema.GetProperty("properties");
        Assert.True(properties.TryGetProperty("userQuestion", out _));
        Assert.False(properties.TryGetProperty("query", out _));
        Assert.False(properties.TryGetProperty("limit", out _));
        Assert.Contains("Policy", client.Result, StringComparison.Ordinal);
        Assert.Contains("Approved process", client.Result, StringComparison.Ordinal);
    }

    private sealed class SearchClient : IChatClient
    {
        private int _calls;
        internal AITool? SearchTool { get; private set; }
        internal string? Result { get; private set; }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_calls++ == 0)
            {
                SearchTool = Assert.Single(options!.Tools!, tool => tool.Name == "search_knowledge_base");
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent("search", "search_knowledge_base", new Dictionary<string, object?>
                    {
                        ["userQuestion"] = "policy"
                    })]);
            }
            else
            {
                Result = Assert.Single(messages.SelectMany(message => message.Contents)
                    .OfType<FunctionResultContent>()).Result?.ToString();
                yield return new ChatResponseUpdate(ChatRole.Assistant, "Found.");
            }
            await Task.CompletedTask;
        }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceKey == null && serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose() { }
    }
}
