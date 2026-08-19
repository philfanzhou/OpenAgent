using OpenAgent.Router.Endpoints;
using Xunit;

namespace OpenAgent.Router.Tests.Endpoints;

public class ForwardingContextBuilderTests
{
    [Fact]
    public async Task ApplyAsync_PreservesApplicationHeaders()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://router/chat");
        request.Headers.Add("X-Agent-Id", "finance");
        request.Headers.Add("X-Tenant-Id", "tenant-1");
        request.Headers.Add("X-User-Id", "user-1");
        request.Headers.Add("X-Conversation-Id", "conversation-1");
        await ForwardingContextBuilder.ApplyAsync(
            request,
            new Uri("http://engine/api/v1/agent/chat"),
            "trace-1");

        Assert.Equal("finance", Assert.Single(request.Headers.GetValues("X-Agent-Id")));
        Assert.Equal("tenant-1", Assert.Single(request.Headers.GetValues("X-Tenant-Id")));
        Assert.Equal("user-1", Assert.Single(request.Headers.GetValues("X-User-Id")));
        Assert.Equal("conversation-1", Assert.Single(request.Headers.GetValues("X-Conversation-Id")));
        Assert.Equal("trace-1", Assert.Single(request.Headers.GetValues("X-Trace-Id")));
    }

    [Fact]
    public async Task ApplyAsync_PreservesTenantHeader()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://router/chat");
        request.Headers.Add("X-Tenant-Id", "spoofed-tenant");

        await ForwardingContextBuilder.ApplyAsync(
            request,
            new Uri("http://engine/api/v1/agent/chat"),
            "trace-1");

        string? actual = request.Headers.TryGetValues(
            "X-Tenant-Id",
            out IEnumerable<string>? values)
            ? Assert.Single(values)
            : null;
        Assert.Equal("spoofed-tenant", actual);
    }
}
