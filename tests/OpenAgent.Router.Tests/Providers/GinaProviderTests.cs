using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Security;
using OpenAgent.Router.Models;
using OpenAgent.Router.Providers;
using Xunit;

namespace OpenAgent.Router.Tests.Providers;

public class GinaProviderTests
{
    [Fact]
    public async Task GetAgentsAsync_ParsesGinaAgentListAndUsesServerToken()
    {
        var handler = new RecordingHandler(
            "{\"agents\":[{\"id\":\"general\",\"name\":\"General\",\"description\":\"General assistant\"}]}");
        using GinaProvider provider = CreateProvider(handler);

        AgentProviderCatalog catalog = await provider.GetAgentsAsync(
            RequestContext(),
            CancellationToken.None);

        AgentSummary agent = Assert.Single(catalog.Agents);
        Assert.Equal("general", agent.AgentId);
        Assert.Equal("General", agent.Name);
        Assert.Equal("General assistant", agent.Description);
        Assert.Equal("https://gina.example/api/agentlist", handler.RequestUri);
        Assert.Equal("Bearer server-token", handler.Authorization);
    }

    [Fact]
    public async Task ConfigureRequestAsync_MapsRouterHeadersAndKeepsGinaChatPath()
    {
        using GinaProvider provider = CreateProvider(new RecordingHandler("[]"));
        AgentForwardingTarget? target = await provider.ResolveForwardingAsync(
            "stream",
            "tenant-1",
            "conversation-1",
            CancellationToken.None);
        Assert.NotNull(target);
        using var request = new HttpRequestMessage(HttpMethod.Post, target!.RequestUri);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer caller-token");
        request.Headers.TryAddWithoutValidation("X-Agent-Id", "general");
        request.Headers.TryAddWithoutValidation("X-Gina-Session-Id", "gina-session-1");
        request.Headers.TryAddWithoutValidation("Accept", "text/event-stream");

        await provider.ConfigureRequestAsync(request, target, CancellationToken.None);

        Assert.Equal("https://gina.example/api/chat", target.RequestUri.ToString());
        Assert.Equal("Bearer server-token", request.Headers.GetValues("Authorization").Single());
        Assert.Equal("general", request.Headers.GetValues("X-Gina-Agent-Id").Single());
        Assert.Equal("gina-session-1", request.Headers.GetValues("X-Gina-Session-Id").Single());
        Assert.Equal("text/event-stream", request.Headers.GetValues("Accept").Single());
    }

    [Fact]
    public async Task ConfigureRequestAsync_UsesRouterConversationAsGinaSessionWhenNeeded()
    {
        using GinaProvider provider = CreateProvider(new RecordingHandler("[]"));
        AgentForwardingTarget? target = await provider.ResolveForwardingAsync(
            null,
            null,
            null,
            CancellationToken.None);
        Assert.NotNull(target);
        using var request = new HttpRequestMessage(HttpMethod.Post, target!.RequestUri);
        request.Headers.TryAddWithoutValidation("X-Agent-Id", "general");
        request.Headers.TryAddWithoutValidation("X-Conversation-Id", "conversation-1");

        await provider.ConfigureRequestAsync(request, target, CancellationToken.None);

        Assert.Equal("conversation-1", request.Headers.GetValues("X-Gina-Session-Id").Single());
    }

    private static GinaProvider CreateProvider(HttpMessageHandler handler) =>
        new(
            "gina",
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["BaseUrl"] = "https://gina.example/",
                    ["DefaultAgentId"] = "general",
                    ["ServerToken"] = "server-token"
                })
                .Build(),
            handler);

    private static AgentProviderRequestContext RequestContext() =>
        new(
            new AgentUserContext
            {
                UserId = "user-1",
                TenantId = "tenant-1",
                IsAuthenticated = true
            },
            "Bearer caller-token");

    private sealed class RecordingHandler(string responseBody) : HttpMessageHandler
    {
        public string? RequestUri { get; private set; }
        public string? Authorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri?.ToString();
            Authorization = request.Headers.TryGetValues(
                "Authorization",
                out IEnumerable<string>? values)
                ? values.SingleOrDefault()
                : null;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            });
        }
    }
}
