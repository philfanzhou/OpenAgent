using System.Text.Json;
using OpenAgent.Contracts.Capabilities;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Core.Abstract;
using OpenAgent.Core.Capabilities;
using OpenAgent.Core.Conversation.Store;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Capabilities.Context;
using OpenAgent.Core.Capabilities.Web;
using OpenAgent.Core.Conversation;
using OpenAgent.Core.Files;
using OpenAgent.Core.Tests.TestDoubles;
using Microsoft.Extensions.AI;
using Xunit;

namespace OpenAgent.Core.Tests.Capabilities;

public class WebAndContextCapabilityTests
{
    // ---- web_fetch ----

    [Fact]
    public async Task WebFetch_HtmlPage_ReturnsExtractedTextWithMetadata()
    {
        var fetcher = new FakeFetcher("""<html><head><style>.x{}</style></head><body><h1>API Reference</h1><p>Use <b>POST /v1/items</b> to create.</p><script>alert(1)</script></body></html>""", "text/html; charset=utf-8");
        var source = new WebCapabilitySource(fetcher);

        ToolResult result = await InvokeAsync(source, "web_fetch",
            new Dictionary<string, object?> { ["url"] = "https://example.com/docs" });

        Assert.False(result.IsError);
        using JsonDocument document = JsonDocument.Parse(result.Content);
        Assert.Equal("text/html", document.RootElement.GetProperty("contentType").GetString());
        Assert.False(document.RootElement.GetProperty("cached").GetBoolean());
        string text = document.RootElement.GetProperty("text").GetString()!;
        Assert.Contains("API Reference", text, StringComparison.Ordinal);
        Assert.Contains("POST /v1/items", text, StringComparison.Ordinal);
        Assert.DoesNotContain("alert", text, StringComparison.Ordinal);
        Assert.DoesNotContain(".x{}", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WebFetch_SecondCallWithinTtl_ServedFromCache()
    {
        var fetcher = new FakeFetcher("<html><body><p>cached page</p></body></html>", "text/html");
        var source = new WebCapabilitySource(fetcher);

        await InvokeAsync(source, "web_fetch", new Dictionary<string, object?> { ["url"] = "https://a.example/x" });
        ToolResult second = await InvokeAsync(source, "web_fetch",
            new Dictionary<string, object?> { ["url"] = "https://a.example/x" });

        using JsonDocument document = JsonDocument.Parse(second.Content);
        Assert.True(document.RootElement.GetProperty("cached").GetBoolean());
        Assert.Equal(1, fetcher.Calls);
    }

    [Fact]
    public async Task WebFetch_BinaryContent_ReturnsActionableEnvelope()
    {
        var source = new WebCapabilitySource(new FakeFetcher([1, 2, 3], "application/pdf"));

        ToolResult result = await InvokeAsync(source, "web_fetch",
            new Dictionary<string, object?> { ["url"] = "https://a.example/report.pdf" });

        Assert.True(result.IsError);
        using JsonDocument document = JsonDocument.Parse(result.Content);
        Assert.Equal("unsupported_content", document.RootElement.GetProperty("code").GetString());
        Assert.Contains("download_file", document.RootElement.GetProperty("hint").GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ftp://example.com/x")]
    [InlineData("not-a-url")]
    public async Task WebFetch_InvalidUrl_ReturnsInvalidArguments(string url)
    {
        var source = new WebCapabilitySource(new FakeFetcher("x", "text/plain"));

        ToolResult result = await InvokeAsync(source, "web_fetch", new Dictionary<string, object?> { ["url"] = url });

        Assert.True(result.IsError);
        using JsonDocument document = JsonDocument.Parse(result.Content);
        Assert.Equal("invalid_arguments", document.RootElement.GetProperty("code").GetString());
    }

    // ---- get_context_remaining ----

    [Fact]
    public async Task ContextRemaining_SumsPersistedUsageAndEstimates()
    {
        var store = new InMemoryConversationStore(new FakeCurrentUser("user-a"));
        var record = new ConversationRecord
        {
            TenantId = "tenant-a",
            ConversationId = "conv-1",
            UserId = "user-a",
            Type = ConversationType.User,
            AgentId = "agent-1",
            Status = ConversationStatus.Completed
        };
        record.Messages.Add(new ConversationMessage
        {
            MessageId = "m1", Sequence = 1, Role = "user", Content = "hello", Timestamp = DateTimeOffset.Parse("2026-01-01T00:00:00Z")
        });
        record.Messages.Add(new ConversationMessage
        {
            MessageId = "m2", Sequence = 2, Role = "assistant", Content = new string('x', 400),
            Timestamp = DateTimeOffset.Parse("2026-01-01T00:00:01Z"),
            TokenUsage = new OpenAgent.Contracts.Requests.TokenUsage { PromptTokens = 10, CompletionTokens = 20, TotalTokens = 30 }
        });
        await store.CreateAsync(record);
        var context = new FileAssetExecutionContext();
        context.Set(TurnContexts.Create("tenant-a", "user-a", "conv-1"));
        var source = new ContextCapabilitySource(store, context, new RunModelContext { ContextTokens = 1000 });

        ToolResult result = await InvokeAsync(source, "get_context_remaining", new Dictionary<string, object?>());

        using JsonDocument document = JsonDocument.Parse(result.Content);
        Assert.True(document.RootElement.GetProperty("available").GetBoolean());
        Assert.Equal(1000, document.RootElement.GetProperty("contextTokens").GetInt32());
        // m2 有记录用量按 30 计；m1 "hello"(5B→1) 无记录按估算 → 31。
        Assert.Equal(31, document.RootElement.GetProperty("usedTokensApprox").GetInt64());
        Assert.Equal(969, document.RootElement.GetProperty("remainingTokensApprox").GetInt64());
    }

    [Fact]
    public async Task ContextRemaining_UnconfiguredWindow_ReportsUnavailable()
    {
        var context = new FileAssetExecutionContext();
        context.Set(TurnContexts.Create("tenant-a", "user-a", "conv-1"));
        var source = new ContextCapabilitySource(
            new InMemoryConversationStore(new FakeCurrentUser("user-a")), context, new RunModelContext());

        ToolResult result = await InvokeAsync(source, "get_context_remaining", new Dictionary<string, object?>());

        using JsonDocument document = JsonDocument.Parse(result.Content);
        Assert.False(document.RootElement.GetProperty("available").GetBoolean());
    }

    // ---- Qwen 兼容包装 ----

    [Fact]
    public void EnsureUserMessage_AppendsPlaceholderOnlyWhenMissing()
    {
        List<ChatMessage> assistantOnly = [new ChatMessage(ChatRole.Assistant, "summarize")];
        List<ChatMessage> withUser =
        [
            new ChatMessage(ChatRole.System, "sys"),
            new ChatMessage(ChatRole.User, "hi"),
            new ChatMessage(ChatRole.Assistant, "ok")
        ];

        IReadOnlyList<ChatMessage> patched = UserMessageEnsuringChatClient.EnsureUserMessage(assistantOnly);
        Assert.Equal(2, patched.Count);
        Assert.Equal(ChatRole.User, patched[^1].Role);

        IReadOnlyList<ChatMessage> unchanged = UserMessageEnsuringChatClient.EnsureUserMessage(withUser);
        Assert.Equal(3, unchanged.Count);
        Assert.Same(withUser[1], unchanged[1]);
    }

    private static async Task<ToolResult> InvokeAsync(
        OpenAgent.Core.Capabilities.ICapabilitySource source,
        string toolName,
        IReadOnlyDictionary<string, object?> arguments)
    {
        IReadOnlyList<OpenAgent.Core.Capabilities.CapabilityDefinition> definitions = await source.DiscoverAsync(
            "agent-1", new AgentConfig(), User, CancellationToken.None);
        var definition = Assert.Single(definitions, item => item.Name == toolName);
        return await definition.Invoke(new Dictionary<string, object?>(arguments), CancellationToken.None);
    }

    private sealed class FakeCurrentUser(string userId) : ICurrentUserContext
    {
        public string UserId => userId;
        public string? TenantId => null;
        public bool IsAuthenticated => true;
        public IReadOnlyList<string> Roles => [];
        public bool IsInRole(string role) => false;
    }

    private static readonly AgentUserContext User = new()
    {
        UserId = "user-a",
        TenantId = "tenant-a",
        Claims = new Dictionary<string, string>(),
        IsAuthenticated = true
    };

    private sealed class FakeFetcher(string content, string mediaType) : IWebFetcher
    {
        public int Calls { get; private set; }

        public FakeFetcher(byte[] content, string mediaType) : this(
            System.Text.Encoding.UTF8.GetString(content), mediaType)
        {
        }

        public Task<DownloadedFile> FetchAsync(string url, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new DownloadedFile("page", mediaType,
                System.Text.Encoding.UTF8.GetBytes(content)));
        }
    }
}
