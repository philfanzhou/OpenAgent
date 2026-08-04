using OpenAgent.Core.Abstract;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Engine;
using OpenAgent.Core.Impl.Compression;
using OpenAgent.Core.Conversation.Compression;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace OpenAgent.Core.Tests.Compression;

public class CompressionTests
{
    [Fact]
    public void PublicCompressionTypes_RemainInCompatibilityNamespace()
    {
        const string compatibilityNamespace = "OpenAgent.Core.Impl.Compression";

        Assert.Equal(compatibilityNamespace, typeof(IContextCompressor).Namespace);
        Assert.Equal(compatibilityNamespace, typeof(CompressionResult).Namespace);
        Assert.Equal(compatibilityNamespace, typeof(CompressionMetrics).Namespace);
    }

    #region SlidingWindowCompressor

    [Fact]
    public async Task SlidingWindow_TotalTokensLessThanWindow_ReturnsFullHistory()
    {
        var compressor = new SlidingWindowCompressor();
        var history = CreateMessages(3);
        var policy = new ContextPolicy { Strategy = "sliding_window", MaxTokens = 100 };

        var result = await compressor.CompressAsync(history, policy, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(3, result.CompressedMessages.Count);
    }

    [Fact]
    public async Task SlidingWindow_TotalTokensGreaterThanWindow_KeepsRecentN()
    {
        var compressor = new SlidingWindowCompressor();
        var history = CreateMessages(5);
        var policy = new ContextPolicy { Strategy = "sliding_window", MaxTokens = 8 };

        var result = await compressor.CompressAsync(history, policy, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(2, result.CompressedMessages.Count);
        Assert.Equal("msg-4", result.CompressedMessages[0].MessageId);
        Assert.Equal("msg-5", result.CompressedMessages[1].MessageId);
    }

    [Fact]
    public async Task SlidingWindow_SingleMessageExceedsWindow_KeepsOne()
    {
        var compressor = new SlidingWindowCompressor();
        var history = CreateMessages(3);
        var policy = new ContextPolicy { Strategy = "sliding_window", MaxTokens = 1 };

        var result = await compressor.CompressAsync(history, policy, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Single(result.CompressedMessages);
        Assert.Equal("msg-3", result.CompressedMessages[0].MessageId);
    }

    [Fact]
    public async Task SlidingWindow_EmptyHistory_ReturnsEmpty()
    {
        var compressor = new SlidingWindowCompressor();
        var policy = new ContextPolicy { Strategy = "sliding_window", MaxTokens = 100 };

        var result = await compressor.CompressAsync(Array.Empty<ConversationMessage>(), policy, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(result.CompressedMessages);
    }

    [Fact]
    public async Task SlidingWindow_BoundaryEqualsWindow_ReturnsFullHistory()
    {
        var compressor = new SlidingWindowCompressor();
        var history = CreateMessages(3);
        var policy = new ContextPolicy { Strategy = "sliding_window", MaxTokens = 12 };

        var result = await compressor.CompressAsync(history, policy, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(3, result.CompressedMessages.Count);
    }

    #endregion

    #region SummarizeCompressor

    [Fact]
    public async Task Summarize_LlmSuccess_ReturnsSummaryMessage()
    {
        var engine = new FixedChatEngine("summary-result");
        var compressor = new SummarizeCompressor(engine.ChatCompletionAsync, NullLogger<SummarizeCompressor>.Instance);
        var history = CreateTurns(turnCount: 3);
        var policy = new ContextPolicy { Strategy = "summarize", PreserveRecentTurns = 0 };

        var result = await compressor.CompressAsync(history, policy, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Single(result.CompressedMessages, m => m.Role == "summary");
        var summaryMessage = result.CompressedMessages.First(m => m.Role == "summary");
        Assert.Equal("summary-result", summaryMessage.Content);
        Assert.NotNull(result.Summary);
        Assert.Equal("summary-result", result.Summary!.Summary);
    }

    [Fact]
    public async Task Summarize_LlmThrows_PropagatesCompressionException()
    {
        var engine = new ThrowingChatEngine();
        var compressor = new SummarizeCompressor(engine.ChatCompletionAsync, NullLogger<SummarizeCompressor>.Instance);
        var history = CreateTurns(turnCount: 3);
        var policy = new ContextPolicy { Strategy = "summarize", PreserveRecentTurns = 0 };

        var ex = await Assert.ThrowsAsync<CompressionException>(() => compressor.CompressAsync(history, policy, CancellationToken.None));
        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }

    [Fact]
    public async Task Summarize_LlmReturnsEmpty_FallsBackToOriginalMessages()
    {
        var engine = new FixedChatEngine(string.Empty);
        var compressor = new SummarizeCompressor(engine.ChatCompletionAsync, NullLogger<SummarizeCompressor>.Instance);
        var history = CreateTurns(turnCount: 2);
        var policy = new ContextPolicy { Strategy = "summarize", PreserveRecentTurns = 0 };

        var result = await compressor.CompressAsync(history, policy, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(history.Count, result.CompressedMessages.Count);
        Assert.DoesNotContain(result.CompressedMessages, m => m.Role == "summary");
    }

    [Fact]
    public async Task Summarize_SummaryMessage_HasSummaryRole()
    {
        var engine = new FixedChatEngine("summary-text");
        var compressor = new SummarizeCompressor(engine.ChatCompletionAsync, NullLogger<SummarizeCompressor>.Instance);
        var history = CreateTurns(turnCount: 3);
        var policy = new ContextPolicy { Strategy = "summarize", PreserveRecentTurns = 0 };

        var result = await compressor.CompressAsync(history, policy, CancellationToken.None);

        Assert.Contains(result.CompressedMessages, m => m.Role == "summary");
    }

    [Fact]
    public async Task Summarize_PreserveRecentTurns_KeepsLastNTurns()
    {
        var engine = new FixedChatEngine("summary-text");
        var compressor = new SummarizeCompressor(engine.ChatCompletionAsync, NullLogger<SummarizeCompressor>.Instance);
        var history = CreateTurns(turnCount: 4);
        var policy = new ContextPolicy { Strategy = "summarize", PreserveRecentTurns = 1 };

        var result = await compressor.CompressAsync(history, policy, CancellationToken.None);

        Assert.True(result.Success);
        var recentUserMessages = result.CompressedMessages.Where(m => m.Role == "user").ToList();
        Assert.Single(recentUserMessages);
        Assert.Equal("user-4", recentUserMessages[0].MessageId);
    }

    #endregion

    #region ContextCompressorDispatcher

    [Fact]
    public async Task Dispatcher_StrategySliding_CallsSlidingWindow()
    {
        var dispatcher = CreateDispatcher();
        var history = CreateMessages(5);
        var policy = new ContextPolicy { Strategy = "sliding_window", MaxTokens = 8 };

        var result = await dispatcher.CompressAsync(history, policy, truncateLimit: 2);

        Assert.True(result.Success);
        Assert.Equal(2, result.CompressedMessages.Count);
    }

    [Fact]
    public async Task Dispatcher_StrategySummarize_CallsSummarize()
    {
        var engine = new FixedChatEngine("summary-text");
        var dispatcher = CreateDispatcher(summarizeEngine: engine);
        var history = CreateTurns(turnCount: 3);
        var policy = new ContextPolicy { Strategy = "summarize", PreserveRecentTurns = 0 };

        var result = await dispatcher.CompressAsync(history, policy, truncateLimit: 2);

        Assert.True(result.Success);
        Assert.Contains(result.CompressedMessages, m => m.Role == "summary");
    }

    [Fact]
    public async Task Dispatcher_UnknownStrategy_FallsBackToTruncate()
    {
        var dispatcher = CreateDispatcher();
        var history = CreateMessages(5);
        var policy = new ContextPolicy { Strategy = "unknown", MaxTokens = 100 };

        var result = await dispatcher.CompressAsync(history, policy, truncateLimit: 2);

        Assert.True(result.Success);
        Assert.Equal(2, result.CompressedMessages.Count);
        Assert.Equal("msg-4", result.CompressedMessages[0].MessageId);
    }

    [Fact]
    public async Task Dispatcher_CompressorThrows_FallsBackToTruncateAndLogsWarning()
    {
        var logger = new CaptureLogger<ContextCompressorDispatcher>();
        var dispatcher = new ContextCompressorDispatcher(
            new[] { new ThrowingCompressor() },
            logger,
            new CompressionMetrics());
        var history = CreateMessages(5);
        var policy = new ContextPolicy { Strategy = "throwing", MaxTokens = 100 };

        var result = await dispatcher.CompressAsync(history, policy, truncateLimit: 2);

        Assert.True(result.Success);
        Assert.Equal(2, result.CompressedMessages.Count);
        Assert.Contains(logger.LogEntries, e => e.LogLevel == LogLevel.Warning);
    }

    [Fact]
    public async Task Dispatcher_NullPolicy_ReturnsFullHistory()
    {
        var dispatcher = CreateDispatcher();
        var history = CreateMessages(5);

        var result = await dispatcher.CompressAsync(history, null, truncateLimit: 10);

        Assert.True(result.Success);
        Assert.Equal(5, result.CompressedMessages.Count);
    }

    #endregion

    #region TurnSplitter

    [Fact]
    public void TurnSplitter_EmptyMessages_ReturnsEmpty()
    {
        var result = TurnSplitter.Split(Array.Empty<ConversationMessage>());
        Assert.Empty(result);
    }

    [Fact]
    public void TurnSplitter_SingleUserMessage_ReturnsOneTurn()
    {
        var messages = new[]
        {
            new ConversationMessage { MessageId = "1", Sequence = 1, Role = "user", Content = "hello" }
        };

        var result = TurnSplitter.Split(messages);
        Assert.Single(result);
        Assert.Single(result[0]);
        Assert.Equal("user", result[0][0].Role);
    }

    [Fact]
    public void TurnSplitter_AssistantOnlyMessages_NoTurns()
    {
        var messages = new[]
        {
            new ConversationMessage { MessageId = "1", Sequence = 1, Role = "assistant", Content = "hi" },
            new ConversationMessage { MessageId = "2", Sequence = 2, Role = "assistant", Content = "there" }
        };

        var result = TurnSplitter.Split(messages);
        Assert.Empty(result);
    }

    [Fact]
    public void TurnSplitter_MultipleTurns_SplitsCorrectly()
    {
        var messages = new[]
        {
            new ConversationMessage { MessageId = "1", Sequence = 1, Role = "user", Content = "a" },
            new ConversationMessage { MessageId = "2", Sequence = 2, Role = "assistant", Content = "b" },
            new ConversationMessage { MessageId = "3", Sequence = 3, Role = "user", Content = "c" },
            new ConversationMessage { MessageId = "4", Sequence = 4, Role = "tool", Content = "d" },
            new ConversationMessage { MessageId = "5", Sequence = 5, Role = "assistant", Content = "e" },
        };

        var result = TurnSplitter.Split(messages);
        Assert.Equal(2, result.Count);
        Assert.Equal(2, result[0].Count); // user + assistant
        Assert.Equal(3, result[1].Count); // user + tool + assistant
    }

    [Fact]
    public void TurnSplitter_SummaryMessages_NotStartingTurn()
    {
        // Summary before user is not collected (currentTurn is null until first user message)
        var messages = new[]
        {
            new ConversationMessage { MessageId = "1", Sequence = 1, Role = "summary", Content = "prev" },
            new ConversationMessage { MessageId = "2", Sequence = 2, Role = "user", Content = "hello" },
            new ConversationMessage { MessageId = "3", Sequence = 3, Role = "assistant", Content = "hi" }
        };

        var result = TurnSplitter.Split(messages);
        Assert.Single(result); // Only one turn starting from "user"
        Assert.Equal(2, result[0].Count); // user + assistant (summary is orphaned)
    }

    #endregion

    #region CompressionMetrics

    [Fact]
    public void CompressionMetrics_TracksSuccessAndFallback()
    {
        var metrics = new CompressionMetrics();
        metrics.RecordCompressionStart();
        metrics.RecordSuccess("summarize", 5);
        metrics.RecordCompressionStart();
        metrics.RecordFallbackTruncation();
        metrics.RecordLatency(100);

        // Just verify no exceptions — snapshot logging is informational
        metrics.LogSnapshot(NullLogger.Instance);
    }

    #endregion

    #region Helpers

    private static IReadOnlyList<ConversationMessage> CreateMessages(int count)
    {
        var messages = new List<ConversationMessage>();
        for (int i = 1; i <= count; i++)
        {
            messages.Add(new ConversationMessage
            {
                MessageId = $"msg-{i}",
                Sequence = i,
                Role = i % 2 == 1 ? "user" : "assistant",
                Content = $"content-{i}"
            });
        }
        return messages;
    }

    private static IReadOnlyList<ConversationMessage> CreateTurns(int turnCount)
    {
        var messages = new List<ConversationMessage>();
        int seq = 1;
        for (int i = 1; i <= turnCount; i++)
        {
            messages.Add(new ConversationMessage
            {
                MessageId = $"user-{i}",
                Sequence = seq++,
                Role = "user",
                Content = $"user-content-{i}"
            });
            messages.Add(new ConversationMessage
            {
                MessageId = $"assistant-{i}",
                Sequence = seq++,
                Role = "assistant",
                Content = $"assistant-content-{i}"
            });
        }
        return messages;
    }

    private static ContextCompressorDispatcher CreateDispatcher(ITestModelRuntime? summarizeEngine = null)
    {
        var compressors = new List<IContextCompressor>
        {
            new SlidingWindowCompressor(),
            new SummarizeCompressor(
                (summarizeEngine ?? new FixedChatEngine("summary")).ChatCompletionAsync,
                NullLogger<SummarizeCompressor>.Instance)
        };
        return new ContextCompressorDispatcher(compressors, NullLogger<ContextCompressorDispatcher>.Instance, new CompressionMetrics());
    }

    private sealed class FixedChatEngine : ITestModelRuntime
    {
        private readonly string _content;

        public FixedChatEngine(string content) => _content = content;

        public Task<EngineChatCompletionResult> ChatCompletionAsync(EngineChatRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new EngineChatCompletionResult { Content = _content });

        public IAsyncEnumerable<EngineChatCompletionChunk> StreamingChatCompletionAsync(EngineChatRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

    }

    private sealed class ThrowingChatEngine : ITestModelRuntime
    {
        public Task<EngineChatCompletionResult> ChatCompletionAsync(EngineChatRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("LLM failed");

        public IAsyncEnumerable<EngineChatCompletionChunk> StreamingChatCompletionAsync(EngineChatRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

    }

    private sealed class ThrowingCompressor : IContextCompressor
    {
        public string StrategyName => "throwing";

        public Task<CompressionResult> CompressAsync(IReadOnlyList<ConversationMessage> history, ContextPolicy policy, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Compressor failed");
    }

    private sealed class CaptureLogger<T> : ILogger<T>
    {
        public List<(LogLevel LogLevel, string Message)> LogEntries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            LogEntries.Add((logLevel, formatter(state, exception)));
        }
    }

    #endregion
}
