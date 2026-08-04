using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Middleware;
using Xunit;

namespace OpenAgent.Core.Tests.Middleware;

public class AuditLoggingTests
{
    private readonly AuditLogging _middleware = new(NullLogger<AuditLogging>.Instance);

    [Fact]
    public async Task InvokeAsync_logs_and_passes_through_on_success()
    {
        var request = CreateRequest();
        var userContext = CreateUserContext();

        var response = await _middleware.InvokeAsync(request, userContext, NextDelegate, CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal("next-called", response.Content);
        Assert.True(response.Success);
    }

    [Fact]
    public async Task InvokeAsync_UsesInvocationScopeForCommonLogFields()
    {
        var logger = new CaptureLogger<AuditLogging>();
        var middleware = new AuditLogging(logger);
        var request = CreateRequest("conv-1", "trace-1");
        var userContext = CreateUserContext();

        await middleware.InvokeAsync(request, userContext, NextDelegate, CancellationToken.None);

        var startEntry = Assert.Single(logger.Entries.Where(entry =>
            entry.Properties.TryGetValue("{OriginalFormat}", out var format)
            && string.Equals(format?.ToString(), "[Audit] Agent request started. StartedAt={StartedAt}", StringComparison.Ordinal)));

        Assert.Equal(LogLevel.Information, startEntry.LogLevel);
        Assert.Contains(startEntry.Properties, kv => kv.Key == "StartedAt");

        var completedEntry = Assert.Single(logger.Entries.Where(entry =>
            entry.Properties.TryGetValue("{OriginalFormat}", out var format)
            && string.Equals(format?.ToString(), "[Audit] Agent request completed. Streaming={Streaming}, Success={Success}, ErrorCode={ErrorCode}, PromptTokens={PromptTokens}, CompletionTokens={CompletionTokens}, DurationMs={DurationMs}", StringComparison.Ordinal)));

        Assert.Equal(LogLevel.Information, completedEntry.LogLevel);
        Assert.Equal(true, completedEntry.Properties["Success"]);
    }

    [Fact]
    public async Task InvokeAsync_rethrows_downstream_exception()
    {
        var request = CreateRequest();
        var userContext = CreateUserContext();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _middleware.InvokeAsync(request, userContext, ThrowingNextDelegate, CancellationToken.None));

        Assert.Equal("downstream failure", ex.Message);
    }

    [Fact]
    public async Task InvokeStreamAsync_passes_through_chunks()
    {
        var request = CreateRequest();
        var userContext = CreateUserContext();

        var chunks = new List<string>();
        await foreach (var chunk in _middleware.InvokeStreamAsync(request, userContext, StreamNextDelegate, CancellationToken.None))
        {
            chunks.Add(chunk);
        }

        Assert.Equal(new[] { "chunk-a", "chunk-b", "chunk-c" }, chunks);
    }

    private static AgentRequest CreateRequest(string? conversationId = null, string? traceId = null) => new()
    {
        Query = "test query",
        AgentId = "agent-1",
        ConversationId = conversationId,
        TraceId = traceId
    };

    private static AgentUserContext CreateUserContext() => new()
    {
        UserId = "user-1",
        TenantId = "tenant-1",
        IsAuthenticated = true
    };

    private static Task<AgentResponse> NextDelegate(AgentRequest request, IAgentUserContext userContext, CancellationToken ct)
    {
        return Task.FromResult(new AgentResponse { Content = "next-called", Success = true });
    }

    private static Task<AgentResponse> ThrowingNextDelegate(AgentRequest request, IAgentUserContext userContext, CancellationToken ct)
    {
        throw new InvalidOperationException("downstream failure");
    }

    private static IAsyncEnumerable<string> StreamNextDelegate(AgentRequest request, IAgentUserContext userContext, CancellationToken ct)
    {
        return StreamChunks("chunk-a", "chunk-b", "chunk-c");
    }

    private static async IAsyncEnumerable<string> StreamChunks(params string[] chunks)
    {
        foreach (var chunk in chunks)
        {
            yield return chunk;
        }
    }
}
