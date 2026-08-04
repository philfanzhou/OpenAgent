using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Security;
using Xunit;

namespace OpenAgent.Core.Tests.Middleware;

public class AgentIdValidationTests
{
    private readonly AgentIdValidation _middleware = new(NullLogger<AgentIdValidation>.Instance);

    [Fact]
    public async Task InvokeAsync_passes_through_when_AgentId_is_present()
    {
        var request = CreateRequest(agentId: "agent-123");
        var userContext = CreateUserContext();

        var response = await _middleware.InvokeAsync(request, userContext, NextDelegate, CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal("next-called", response.Content);
        Assert.True(response.Success);
    }

    [Fact]
    public async Task InvokeAsync_passes_through_when_AgentId_is_missing()
    {
        var request = CreateRequest(agentId: null);
        var userContext = CreateUserContext();

        var response = await _middleware.InvokeAsync(request, userContext, NextDelegate, CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal("next-called", response.Content);
    }

    [Fact]
    public async Task InvokeStreamAsync_passes_through_when_AgentId_is_missing()
    {
        var request = CreateRequest(agentId: null);
        var userContext = CreateUserContext();

        var chunks = new List<string>();
        await foreach (var chunk in _middleware.InvokeStreamAsync(request, userContext, StreamNextDelegate, CancellationToken.None))
        {
            chunks.Add(chunk);
        }

        Assert.Equal(new[] { "chunk-1", "chunk-2" }, chunks);
    }

    private static AgentRequest CreateRequest(string? agentId = null) => new()
    {
        Query = "test query",
        AgentId = agentId
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

    private static IAsyncEnumerable<string> StreamNextDelegate(AgentRequest request, IAgentUserContext userContext, CancellationToken ct)
    {
        return StreamChunks("chunk-1", "chunk-2");
    }

    private static async IAsyncEnumerable<string> StreamChunks(params string[] chunks)
    {
        foreach (var chunk in chunks)
        {
            yield return chunk;
        }
    }
}
