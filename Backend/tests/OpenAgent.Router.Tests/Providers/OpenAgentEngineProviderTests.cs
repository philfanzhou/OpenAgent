using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using OpenAgent.Router.Tests;
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
                ["ServiceHeaders:X-Tenant-Id"] = "intent-tenant"
            })
            .Build();
        using var provider = new OpenAgentEngineProvider(
            "self-engine",
            settings,
            routeTable,
            new TestPermissionServices(),
            new TestPermissionServices(),
            handler);

        IReadOnlyList<AgentSummary> agents = await provider.GetAgentsAsync(
            new AgentUserContext { UserId = "user-1", TenantId = "tenant-1", IsAuthenticated = true },
            CancellationToken.None);
        IntentRecognitionResult? response = await provider.RecognizeIntentAsync(
            "intent-router",
            agents,
            "select an agent",
            new AgentUserContext { UserId = "user-1", TenantId = "tenant-1", IsAuthenticated = true },
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
            ["http://engine/custom/agents", "http://engine/custom/chat"],
            handler.RequestUris);
        Assert.Equal([null, null], handler.TenantIds);
        Assert.Equal([null, null], handler.Authorizations);
        Assert.Equal(["test-gateway-grant", "test-gateway-grant"], handler.GatewayGrants);
        Assert.Equal([null, "intent-router"], handler.ResolvedAgentIds);
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
        public List<string?> TenantIds { get; } = [];
        public List<string?> Authorizations { get; } = [];
        public List<string?> GatewayGrants { get; } = [];
        public List<string?> ResolvedAgentIds { get; } = [];
        public string ChatBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri?.ToString() ?? string.Empty);
            TenantIds.Add(GetHeader(request, "X-Tenant-Id"));
            Authorizations.Add(GetHeader(request, "Authorization"));
            GatewayGrants.Add(GetHeader(request, "X-OpenAgent-Gateway-Grant"));
            ResolvedAgentIds.Add(GetHeader(request, "X-OpenAgent-Resolved-Agent-Id"));
            if (request.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new[]
                    {
                        new AgentSummary { AgentId = "finance" }
                    })
                };
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

        private static string? GetHeader(HttpRequestMessage request, string name) =>
            request.Headers.TryGetValues(name, out IEnumerable<string>? values)
                ? values.SingleOrDefault()
                : null;
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
