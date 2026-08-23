using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Contracts.Approvals;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
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
    public async Task WriteSseStreamAsync_Approval_WritesExistingConversationStreamEvent()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        DateTimeOffset createdAt = DateTimeOffset.Parse("2026-08-20T10:00:00Z");

        await AgentStreamWriter.WriteSseStreamAsync(
            context,
            ApprovalEvents(new HumanApprovalRequest
            {
                ApprovalId = "approval-1",
                TenantId = "tenant-1",
                ConversationId = "conversation-1",
                AgentId = "agent-1",
                Action = "execute",
                TargetType = AgentResourceType.Function,
                TargetCapability = "dangerous_function",
                RedactedArgumentsJson = "{\"apiKey\":\"***\"}",
                RequestedBy = "requester-1",
                CreatedAt = createdAt,
                ExpiresAt = createdAt.AddMinutes(15),
                Status = HumanApprovalStatus.Pending
            }),
            "trace-1",
            "conversation-1",
            NullLogger.Instance,
            CancellationToken.None);

        string payload = await ReadBodyAsync(context);
        Assert.Contains("event: approval", payload, StringComparison.Ordinal);
        Assert.Contains("\"approvalId\":\"approval-1\"", payload, StringComparison.Ordinal);
        Assert.Contains("\"redactedArgumentsJson\":", payload, StringComparison.Ordinal);
        Assert.Contains("\"status\":\"AwaitingApproval\"", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("do-not-expose", payload, StringComparison.Ordinal);
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

    private static async IAsyncEnumerable<AgentStreamEvent> ApprovalEvents(
        HumanApprovalRequest approval)
    {
        yield return new AgentStreamEvent
        {
            Type = AgentStreamEventType.Approval,
            Approval = approval
        };
        await Task.Yield();
        yield return new AgentStreamEvent
        {
            Type = AgentStreamEventType.Usage,
            ModelId = "provider-model"
        };
    }

    private static async Task<string> ReadBodyAsync(HttpContext context)
    {
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        return await new StreamReader(context.Response.Body).ReadToEndAsync();
    }
}
