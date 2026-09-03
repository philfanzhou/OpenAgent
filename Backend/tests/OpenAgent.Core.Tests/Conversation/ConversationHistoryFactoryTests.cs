using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.AI;
using Microsoft.Agents.AI.Compaction;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Files;
using OpenAgent.Core.Conversation;
using OpenAgent.Core.Tests.TestDoubles;
using Xunit;

namespace OpenAgent.Core.Tests.Conversation;

public sealed class ConversationHistoryFactoryTests
{
    [Fact]
    public void CreateStrategy_WithoutPolicy_UsesSummarization()
    {
        ConversationHistoryFactory factory = CreateFactory();

        SummarizationCompactionStrategy strategy = factory.CreateStrategy(
            contextTokens: 1_000,
            policy: null,
            summarizationClient: new FakeChatProvider(new InvalidOperationException("not called")),
            force: false,
            trigger: out _);
        Assert.Equal(2, strategy.MinimumPreservedGroups);
        Assert.IsType<OutputTokenLimitedChatClient>(strategy.ChatClient);
        Assert.Contains("dedicated compression call", strategy.SummarizationPrompt, StringComparison.Ordinal);
        Assert.Contains("HARD LIMIT: the summary must not exceed 200 tokens", strategy.SummarizationPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveAutomaticTokenThreshold_UsesEightyPercentAndFallbackContext()
    {
        ConversationHistoryFactory factory = CreateFactory(defaultContextTokens: 1_000);

        Assert.Equal(800, factory.ResolveAutomaticTokenThreshold(1_000));
        Assert.Equal(80, factory.ResolveAutomaticTokenThreshold(100));
        Assert.Equal(500, factory.ResolveCompactionTargetTokens(1_000));
        Assert.Equal(50, factory.ResolveCompactionTargetTokens(100));
        Assert.Equal(200, factory.ResolveSummaryTokenBudget(1_000, null));
        Assert.Equal(32, factory.ResolveSummaryTokenBudget(100, null));
    }

    [Fact]
    public void ResolveAutomaticTokenThreshold_ConfiguredOverrideWinsOverRatioHeuristic()
    {
        ConversationHistoryFactory factory = CreateFactory(
            defaultContextTokens: 1_000,
            automaticCompactionTokenThreshold: 800_000);

        Assert.Equal(800_000, factory.ResolveAutomaticTokenThreshold(1_000));
        Assert.Equal(800_000, factory.ResolveAutomaticTokenThreshold(100));

        // Target and summary budget remain proportional to the context, not the trigger.
        Assert.Equal(500, factory.ResolveCompactionTargetTokens(1_000));
    }

    [Fact]
    public async Task CreateStrategy_ManualSummarization_CompactsShortHistory()
    {
        ConversationHistoryFactory factory = CreateFactory();
        var client = new CapturingChatClient("short summary");
        SummarizationCompactionStrategy strategy = factory.CreateStrategy(
            contextTokens: 1_000,
            policy: null,
            summarizationClient: client,
            force: true,
            trigger: out _);

        IEnumerable<ChatMessage> result = await CompactionProvider.CompactAsync(
            strategy,
            [
                new ChatMessage(ChatRole.User, "short conversation that the user explicitly requested to compact"),
                new ChatMessage(ChatRole.Assistant, "short completed reply")
            ],
            NullLogger.Instance,
            CancellationToken.None);

        Assert.Equal(0, strategy.MinimumPreservedGroups);
        Assert.NotNull(client.Options);
        Assert.Contains(result, message => message.Text.Contains("short summary", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OutputTokenLimitedChatClient_EnforcesSmallerHardLimit()
    {
        var inner = new CapturingChatClient();
        var client = new OutputTokenLimitedChatClient(inner, 200);

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "summarize")],
            new ChatOptions { MaxOutputTokens = 800 });

        Assert.Equal(800, inner.Options?.MaxOutputTokens);
    }

    [Fact]
    public async Task OutputTokenLimitedChatClient_LimitsPersistedSummaryAfterReasoningAllowance()
    {
        var inner = new CapturingChatClient(new string('摘', 1_000));
        var client = new OutputTokenLimitedChatClient(inner, 128);

        ChatResponse response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "summarize")]);

        Assert.Equal(2_048, inner.Options?.MaxOutputTokens);
        Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(response.Text), 1, 128 * 4);
    }

    [Fact]
    public async Task OutputTokenLimitedChatClient_RejectsEmptySummaryText()
    {
        var client = new OutputTokenLimitedChatClient(new CapturingChatClient(string.Empty), 128);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "summarize")]));

        Assert.Contains("no summary text", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static ConversationHistoryFactory CreateFactory(
        int defaultContextTokens = 1_000,
        int? automaticCompactionTokenThreshold = null) =>
        new(
            store: null!,
            Options.Create(new ConversationStoreOptions
            {
                DefaultModelContextTokens = defaultContextTokens,
                AutomaticCompactionTokenThreshold = automaticCompactionTokenThreshold
            }),
            loggerFactory: NullLoggerFactory.Instance,
            historyFactory: null!);

    private sealed class CapturingChatClient(string responseText = "summary") : IChatClient
    {
        internal ChatOptions? Options { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Options = options;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, responseText)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
