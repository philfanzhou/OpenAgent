using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Security;
using Xunit;

namespace OpenAgent.Core.Tests.Middleware;

public class TenantValidationTests
{
    private readonly TenantValidation _middleware = new(NullLogger<TenantValidation>.Instance);

    [Fact]
    public async Task InvokeAsync_passes_through_when_TenantId_is_present()
    {
        var request = CreateRequest();
        var userContext = CreateUserContext(tenantId: "tenant-1");

        var response = await _middleware.InvokeAsync(request, userContext, NextDelegate, CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal("next-called", response.Content);
    }

    [Fact]
    public async Task InvokeAsync_throws_TenantDataIsolationException_when_TenantId_is_missing()
    {
        var request = CreateRequest();
        var userContext = CreateUserContext(tenantId: null);

        var ex = await Assert.ThrowsAsync<TenantDataIsolationException>(
            () => _middleware.InvokeAsync(request, userContext, NextDelegate, CancellationToken.None));

        Assert.Equal(AgentErrorCode.TenantDataIsolationViolation, ex.ErrorCode);
    }

    [Fact]
    public async Task InvokeStreamAsync_throws_TenantDataIsolationException_when_TenantId_is_missing()
    {
        var request = CreateRequest();
        var userContext = CreateUserContext(tenantId: null);

        await Assert.ThrowsAsync<TenantDataIsolationException>(async () =>
        {
            await foreach (var _ in _middleware.InvokeStreamAsync(request, userContext, StreamNextDelegate, CancellationToken.None))
            {
            }
        });
    }

    private static AgentRequest CreateRequest() => new()
    {
        Query = "test query",
        AgentId = "agent-1"
    };

    private static AgentUserContext CreateUserContext(string? tenantId = "tenant-1") => new()
    {
        UserId = "user-1",
        TenantId = tenantId,
        IsAuthenticated = true
    };

    private static Task<AgentResponse> NextDelegate(AgentRequest request, IAgentUserContext userContext, CancellationToken ct)
    {
        return Task.FromResult(new AgentResponse { Content = "next-called", Success = true });
    }

    private static IAsyncEnumerable<string> StreamNextDelegate(AgentRequest request, IAgentUserContext userContext, CancellationToken ct)
    {
        return StreamChunks("chunk-1");
    }

    private static async IAsyncEnumerable<string> StreamChunks(params string[] chunks)
    {
        foreach (var chunk in chunks)
        {
            yield return chunk;
        }
    }
}
