using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Routing;
using OpenAgent.Contracts.Security;
using OpenAgent.Router.Models;
using OpenAgent.Router.Providers;
using Xunit;

namespace OpenAgent.Router.Tests.Providers;

public class OpenAgentEngineProviderTests
{
    [Fact]
    public async Task Provider_UsesRouteTableContextAndConfiguredProtocolPaths()
    {
        var handler = new RecordingHandler();
        var routeTable = new StubRouteTable();
        IConfiguration settings = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AgentListPath"] = "/custom/agents",
                ["ChatPath"] = "/custom/chat",
                ["ServiceHeaders:Authorization"] = "Basic service-token",
                ["ServiceHeaders:X-Tenant-Id"] = "configured-tenant",
                ["ServiceHeaders:X-User-Id"] = "configured-user"
            })
            .Build();
        using var provider = new OpenAgentEngineProvider(
            "self-engine",
            settings,
            routeTable,
            handler);

        var requestContext = new AgentProviderRequestContext(
            new AgentUserContext
            {
                UserId = "user-1",
                TenantId = "tenant-1",
                IsAuthenticated = true
            },
            "Basic forwarded-token");
        AgentProviderCatalog catalog = await provider.GetAgentsAsync(
            requestContext,
            CancellationToken.None);
        IReadOnlyList<AgentSummary> agents = catalog.Agents;
        IntentRecognitionResult? response = await provider.RecognizeIntentAsync(
            "intent-router",
            agents,
            "select an agent",
            CancellationToken.None);
        AgentProviderConversationStatus conversation = await provider.ResolveConversationAsync(
            requestContext,
            "conversation-1",
            CancellationToken.None);
        AgentForwardingTarget? target = await provider.ResolveForwardingAsync(
            "stream",
            "tenant-1",
            "conversation-1",
            CancellationToken.None);

        Assert.Equal("finance", Assert.Single(agents).AgentId);
        Assert.Equal("finance", response?.AgentId);
        Assert.Equal(0.95, response?.Confidence);
        Assert.Equal(
            [
                "http://engine/custom/agents",
                "http://engine/custom/chat",
                "http://engine/api/v1/agent/provider/conversations/conversation-1"
            ],
            handler.RequestUris);
        Assert.Equal(
            ["Basic forwarded-token", "Basic service-token", "Basic forwarded-token"],
            handler.Authorizations);
        Assert.All(handler.IdentityHeaders, present => Assert.False(present));
        Assert.Equal(AgentProviderConversationStatus.Found, conversation);
        Assert.Equal("tenant-1", routeTable.TenantId);
        Assert.Equal("conversation-1", routeTable.ConversationId);
        Assert.Equal("http://engine", target?.DestinationPrefix);
        Assert.Equal("http://engine/custom/chat/stream", target?.RequestUri.ToString());
        Assert.Contains("intent-router", handler.ChatBody, StringComparison.Ordinal);
        Assert.Contains("select an agent", handler.ChatBody, StringComparison.Ordinal);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> RequestUris { get; } = [];
        public List<string?> Authorizations { get; } = [];
        public List<bool> IdentityHeaders { get; } = [];
        public string ChatBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri?.ToString() ?? string.Empty);
            Authorizations.Add(request.Headers.TryGetValues(
                "Authorization",
                out IEnumerable<string>? authorizations)
                ? authorizations.SingleOrDefault()
                : null);
            IdentityHeaders.Add(
                request.Headers.Contains("X-User-Id")
                || request.Headers.Contains("X-Tenant-Id")
                || request.Headers.Contains("X-OpenAgent-User-Id")
                || request.Headers.Contains("X-OpenAgent-Tenant-Id"));
            if (request.Method == HttpMethod.Get
                && request.RequestUri?.AbsolutePath == "/custom/agents")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new[]
                    {
                        new AgentSummary { AgentId = "finance" }
                    })
                };
            }

            if (request.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            ChatBody = request.Content == null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new ChatResponse
                {
                    Message = "```json\n{\"agentId\":\"finance\",\"confidence\":0.95}\n```"
                })
            };
        }
    }

    private sealed class StubRouteTable : IRouteTable
    {
        public string? TenantId { get; private set; }
        public string? ConversationId { get; private set; }

        public string? GetTargetEndpoint(string intent) => "http://engine";

        public string? GetTargetEndpoint(
            string intent,
            string? tenantId,
            string? conversationId)
        {
            TenantId = tenantId;
            ConversationId = conversationId;
            return "http://engine";
        }
    }
}
