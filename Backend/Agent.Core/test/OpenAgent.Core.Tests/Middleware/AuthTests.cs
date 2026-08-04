using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Security;
using Xunit;

namespace OpenAgent.Core.Tests.Middleware;

public class AuthTests
{
    [Fact]
    public async Task InvokeAsync_passes_through_when_authenticated()
    {
        var middleware = new Auth(new FakePermissionEvaluator(isAuthenticated: true), NullLogger<Auth>.Instance);
        var request = CreateRequest();
        var userContext = CreateUserContext();

        var response = await middleware.InvokeAsync(request, userContext, NextDelegate, CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal("next-called", response.Content);
    }

    [Fact]
    public async Task InvokeAsync_throws_AgentException_when_not_authenticated()
    {
        var middleware = new Auth(new FakePermissionEvaluator(isAuthenticated: false), NullLogger<Auth>.Instance);
        var request = CreateRequest();
        var userContext = CreateUserContext(isAuthenticated: false);

        var ex = await Assert.ThrowsAsync<AgentException>(
            () => middleware.InvokeAsync(request, userContext, NextDelegate, CancellationToken.None));

        Assert.Equal(AgentErrorCode.PermissionDenied, ex.ErrorCode);
    }

    [Fact]
    public async Task InvokeStreamAsync_throws_AgentException_when_not_authenticated()
    {
        var middleware = new Auth(new FakePermissionEvaluator(isAuthenticated: false), NullLogger<Auth>.Instance);
        var request = CreateRequest();
        var userContext = CreateUserContext(isAuthenticated: false);

        await Assert.ThrowsAsync<AgentException>(async () =>
        {
            await foreach (var _ in middleware.InvokeStreamAsync(request, userContext, StreamNextDelegate, CancellationToken.None))
            {
            }
        });
    }

    private static AgentRequest CreateRequest() => new()
    {
        Query = "test query",
        AgentId = "agent-1"
    };

    private static AgentUserContext CreateUserContext(bool isAuthenticated = true) => new()
    {
        UserId = "user-1",
        TenantId = "tenant-1",
        IsAuthenticated = isAuthenticated
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

    private sealed class FakePermissionEvaluator : IPermissionEvaluator
    {
        private readonly bool _isAuthenticated;

        public FakePermissionEvaluator(bool isAuthenticated)
        {
            _isAuthenticated = isAuthenticated;
        }

        public Task<bool> IsAuthenticatedAsync(IAgentUserContext userContext, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_isAuthenticated);
        }
    }
}
