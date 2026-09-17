using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Core.Runtime.Agent;
using OpenAgent.Core.Tests.TestDoubles;
using Xunit;

namespace OpenAgent.Core.Tests.Runtime.Agent;

public sealed class LlmInteractionRecorderTests
{
    [Fact]
    public async Task GetResponseAsync_RecordsSanitizedRequestAndResponse()
    {
        var store = new FakeInteractionStore();
        LlmInteractionRecorder recorder = CreateRecorder(
            store,
            new FakeChatProvider(new ChatResponse(new ChatMessage(ChatRole.Assistant, "你好"))
            {
                ModelId = "gpt-test",
                Usage = new UsageDetails
                {
                    InputTokenCount = 5,
                    OutputTokenCount = 3,
                    TotalTokenCount = 8,
                    CachedInputTokenCount = 2
                }
            }),
            out LlmConfig llm);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "查询订单")
            {
                Contents =
                {
                    new DataContent(new ReadOnlyMemory<byte>([1, 2, 3, 4]), "image/png"),
                    new FunctionCallContent("call-1", "search_orders", new Dictionary<string, object?> { ["orderId"] = 42 })
                }
            }
        };
        var options = new ChatOptions
        {
            ModelId = "gpt-test",
            Temperature = 0.2f,
            Instructions = "system instructions",
            Tools = [new FakeTool("query_database")]
        };

        ChatResponse response = await recorder.GetResponseAsync(messages, options);

        Assert.Equal("你好", response.Text);
        LlmInteractionRecord record = Assert.Single(store.Records);
        Assert.Equal("trace-1", record.TraceId);
        Assert.Equal("conversation-1", record.ConversationId);
        Assert.Equal("tenant-1", record.TenantId);
        Assert.Equal("user-1", record.UserId);
        Assert.Equal(LlmInteractionSource.AgentTurn, record.Source);
        Assert.Equal(LlmInteractionStatus.Succeeded, record.Status);
        Assert.False(record.Streamed);
        Assert.Equal(0, record.CallIndex);
        Assert.Equal(5, record.TokenUsage!.PromptTokens);
        Assert.Equal(2, record.TokenUsage.CachedInputTokens);
        Assert.Equal("gpt-test", record.ModelId);

        var request = JsonDocument.Parse(record.RequestJson!).RootElement;
        Assert.Equal("查询订单", request.GetProperty("messages")[0].GetProperty("text").GetString());
        Assert.Equal("system instructions", request.GetProperty("options").GetProperty("instructions").GetString());
        Assert.Equal(0.2f, request.GetProperty("options").GetProperty("temperature").GetSingle());
        Assert.Equal("query_database", request.GetProperty("options").GetProperty("tools")[0].GetString());

        // 二进制只留占位符，附件字节与 ApiKey 都不得出现。
        string rawRequest = record.RequestJson!;
        Assert.DoesNotContain("secret-api-key", rawRequest);
        Assert.DoesNotContain("AQIDBA==", rawRequest);
        var dataContent = request.GetProperty("messages")[0].GetProperty("contents")
            .EnumerateArray().Single(item => item.GetProperty("kind").GetString() == "data");
        Assert.Equal("image/png", dataContent.GetProperty("mediaType").GetString());
        Assert.Equal(4, dataContent.GetProperty("bytes").GetInt64());
    }

    [Fact]
    public async Task GetStreamingResponseAsync_AccumulatesUpdatesAndUsage()
    {
        var store = new FakeInteractionStore();
        LlmInteractionRecorder recorder = CreateRecorder(
            store,
            new FakeChatProvider(new[]
            {
                new ChatResponseUpdate { Contents = [new TextContent("你")] },
                new ChatResponseUpdate
                {
                    Contents = [new TextContent("好"), new UsageContent(new UsageDetails
                    {
                        InputTokenCount = 7, OutputTokenCount = 2, TotalTokenCount = 9
                    })]
                }
            }),
            out _);

        List<ChatResponseUpdate> received = [];
        await foreach (ChatResponseUpdate update in recorder.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")]))
        {
            received.Add(update);
        }

        Assert.Equal(2, received.Count);
        LlmInteractionRecord record = Assert.Single(store.Records);
        Assert.True(record.Streamed);
        Assert.Equal(LlmInteractionStatus.Succeeded, record.Status);
        Assert.Equal(9, record.TokenUsage!.TotalTokens);
        var response = JsonDocument.Parse(record.ResponseJson!).RootElement;
        Assert.Equal("你好", response.GetProperty("messages")[0].GetProperty("text").GetString());
        Assert.Equal(2, response.GetProperty("updates").GetInt32());
    }

    [Fact]
    public async Task RepeatedCalls_IncrementCallIndex()
    {
        var store = new FakeInteractionStore();
        LlmInteractionRecorder recorder = CreateRecorder(
            store,
            new FakeChatProvider(new ChatResponse(new ChatMessage(ChatRole.Assistant, "a"))),
            out _);

        await recorder.GetResponseAsync([new ChatMessage(ChatRole.User, "1")]);
        await recorder.GetResponseAsync([new ChatMessage(ChatRole.User, "2")]);

        Assert.Equal([0, 1], store.Records.Select(record => record.CallIndex));
    }

    [Fact]
    public async Task ProviderFailure_RecordsFailedAndRethrows()
    {
        var store = new FakeInteractionStore();
        LlmInteractionRecorder recorder = CreateRecorder(
            store,
            new FakeChatProvider(new InvalidOperationException("provider exploded")),
            out _);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => recorder.GetResponseAsync([new ChatMessage(ChatRole.User, "x")]));

        LlmInteractionRecord record = Assert.Single(store.Records);
        Assert.Equal(LlmInteractionStatus.Failed, record.Status);
        Assert.Equal("provider exploded", record.ErrorMessage);
        Assert.Null(record.ResponseJson);
    }

    [Fact]
    public async Task ProviderCancellation_RecordsCancelledAndRethrows()
    {
        var store = new FakeInteractionStore();
        LlmInteractionRecorder recorder = CreateRecorder(
            store,
            new FakeChatProvider(new[]
            {
                new ChatResponseUpdate { Contents = [new TextContent("partial")] }
            }),
            out _);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await foreach (ChatResponseUpdate _ in recorder.GetStreamingResponseAsync(
                [new ChatMessage(ChatRole.User, "x")],
                options: null,
                cancellation.Token))
            {
            }
        });

        LlmInteractionRecord record = Assert.Single(store.Records);
        Assert.Equal(LlmInteractionStatus.Cancelled, record.Status);
        Assert.Equal("cancelled", record.ErrorMessage);
    }

    [Fact]
    public async Task StoreFailure_IsSwallowed()
    {
        LlmInteractionRecorder recorder = CreateRecorder(
            new ThrowingInteractionStore(),
            new FakeChatProvider(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"))),
            out _);

        ChatResponse response = await recorder.GetResponseAsync([new ChatMessage(ChatRole.User, "x")]);

        Assert.Equal("ok", response.Text);
    }

    [Fact]
    public async Task LongFields_AreTruncated()
    {
        var store = new FakeInteractionStore();
        LlmInteractionRecorder recorder = CreateRecorder(
            store,
            new FakeChatProvider(new ChatResponse(new ChatMessage(ChatRole.Assistant, "short"))),
            out _,
            maxContentLength: 20);

        await recorder.GetResponseAsync([new ChatMessage(ChatRole.User, new string('长', 100))]);

        LlmInteractionRecord record = Assert.Single(store.Records);
        string text = JsonDocument.Parse(record.RequestJson!)
            .RootElement.GetProperty("messages")[0].GetProperty("text").GetString()!;
        Assert.True(text.Length < 100);
        Assert.Contains("truncated", text, StringComparison.Ordinal);
    }

    private static LlmInteractionRecorder CreateRecorder(
        ILlmInteractionStore store,
        IChatClient provider,
        out LlmConfig llm,
        int maxContentLength = 100_000)
    {
        llm = new LlmConfig
        {
            TenantId = "tenant-1",
            Provider = "openai",
            Format = ApiFormat.OpenAIChatCompletions,
            ModelId = "configured-model",
            ApiKey = "secret-api-key"
        };
        return new LlmInteractionRecorder(
            provider,
            new LlmInteractionCapture
            {
                TenantId = "tenant-1",
                UserId = "user-1",
                ConversationId = "conversation-1",
                TraceId = "trace-1",
                AgentId = "agent-1",
                Source = LlmInteractionSource.AgentTurn
            },
            llm,
            store,
            new LlmInteractionOptions { MaxContentLength = maxContentLength },
            NullLogger.Instance);
    }

    private sealed class FakeInteractionStore : ILlmInteractionStore
    {
        internal List<LlmInteractionRecord> Records { get; } = [];

        public Task RecordAsync(LlmInteractionRecord record, CancellationToken cancellationToken = default)
        {
            Records.Add(record);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<LlmInteractionRecord>> ListAsync(
            string tenantId,
            string conversationId,
            int skip,
            int take,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LlmInteractionRecord>>([]);
    }

    private sealed class ThrowingInteractionStore : ILlmInteractionStore
    {
        public Task RecordAsync(LlmInteractionRecord record, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("store down");

        public Task<IReadOnlyList<LlmInteractionRecord>> ListAsync(
            string tenantId,
            string conversationId,
            int skip,
            int take,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LlmInteractionRecord>>([]);
    }

    private sealed class FakeTool(string name) : AIFunction
    {
        public override string Name { get; } = name;
        public override string Description => "test tool";
        public override JsonElement JsonSchema { get; } = JsonDocument.Parse("{}").RootElement.Clone();

        protected override ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken) => ValueTask.FromResult<object?>(null);
    }
}
