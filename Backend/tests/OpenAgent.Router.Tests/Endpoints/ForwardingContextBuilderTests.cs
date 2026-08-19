using OpenAgent.Router.Endpoints;
using Xunit;

namespace OpenAgent.Router.Tests.Endpoints;

public class ForwardingContextBuilderTests
{
    [Fact]
    public async Task ApplyAsync_PreservesResolvedAgentHeader()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://router/chat");
        request.Headers.Add("X-Agent-Id", "finance");
        await ForwardingContextBuilder.ApplyAsync(
            request,
            new Uri("http://engine/api/v1/agent/chat"),
            "tenant-1",
            "conversation-1",
            "trace-1",
            forwardDevelopmentTenantHeader: false);

        Assert.Equal("finance", Assert.Single(request.Headers.GetValues("X-Agent-Id")));
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, "trusted-tenant")]
    public async Task ApplyAsync_TenantHeader_OnlyForwardsTrustedDevelopmentValue(
        bool development,
        string? expected)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://router/chat");
        request.Headers.Add("X-Tenant-Id", "spoofed-tenant");

        await ForwardingContextBuilder.ApplyAsync(
            request,
            new Uri("http://engine/api/v1/agent/chat"),
            "trusted-tenant",
            conversationId: null,
            "trace-1",
            development);

        string? actual = request.Headers.TryGetValues(
            "X-Tenant-Id",
            out IEnumerable<string>? values)
            ? Assert.Single(values)
            : null;
        Assert.Equal(expected, actual);
    }
}
