using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Contracts.Requests;
using OpenAgent.Engine.Host.Extensions;
using Xunit;

namespace OpenAgent.Engine.Tests.Hosting;

public class AgentStreamWriterTests
{
    [Fact]
    public async Task WriteSseStreamAsync_TerminalUsage_WritesItOnlyInDoneEvent()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        TokenUsage usage = new()
        {
            PromptTokens = 21,
            CompletionTokens = 8,
            TotalTokens = 29,
            CachedInputTokens = 5,
            ReasoningTokens = 3
        };

        await AgentStreamWriter.WriteSseStreamAsync(
            context,
            Events(usage, "provider-model"),
            "trace-1",
            "conversation-1",
            NullLogger.Instance,
            CancellationToken.None);

        string payload = await ReadBodyAsync(context);
        Assert.DoesNotContain("event: usage", payload, StringComparison.Ordinal);
        Assert.Contains("event: done", payload, StringComparison.Ordinal);
        Assert.Contains(
            "\"usage\":{\"promptTokens\":21,\"completionTokens\":8,\"totalTokens\":29,\"cachedInputTokens\":5,\"reasoningTokens\":3}",
            payload,
            StringComparison.Ordinal);
        Assert.Contains("\"modelId\":\"provider-model\"", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteSseStreamAsync_MissingUsage_WritesExplicitNullInDoneEvent()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await AgentStreamWriter.WriteSseStreamAsync(
            context,
            Events(usage: null, "provider-model"),
            "trace-1",
            "conversation-1",
            NullLogger.Instance,
            CancellationToken.None);

        string payload = await ReadBodyAsync(context);
        Assert.Contains("\"done\":true,\"usage\":null", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteSseStreamAsync_ToolResult_WritesEventWithCallIdAndContent()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await AgentStreamWriter.WriteSseStreamAsync(
            context,
            ToolResultEvents(),
            "trace-1",
            "conversation-1",
            NullLogger.Instance,
            CancellationToken.None);

        string payload = await ReadBodyAsync(context);
        Assert.Contains("event: tool_result", payload, StringComparison.Ordinal);
        Assert.Contains("\"toolCallId\":\"call-1\"", payload, StringComparison.Ordinal);
        Assert.Contains("\"content\":\"sunny\"", payload, StringComparison.Ordinal);
    }

    private static async IAsyncEnumerable<AgentStreamEvent> ToolResultEvents()
    {
        yield return new AgentStreamEvent
        {
            Type = AgentStreamEventType.ToolResult,
            ToolCallId = "call-1",
            Content = "sunny"
        };
        await Task.Yield();
    }

    private static async IAsyncEnumerable<AgentStreamEvent> Events(
        TokenUsage? usage,
        string modelId)
    {
        yield return new AgentStreamEvent
        {
            Type = AgentStreamEventType.Content,
            Content = "hello"
        };
        await Task.Yield();
        yield return new AgentStreamEvent
        {
            Type = AgentStreamEventType.Usage,
            Usage = usage,
            ModelId = modelId
        };
    }

    private static async Task<string> ReadBodyAsync(HttpContext context)
    {
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        return await new StreamReader(context.Response.Body).ReadToEndAsync();
    }
}
