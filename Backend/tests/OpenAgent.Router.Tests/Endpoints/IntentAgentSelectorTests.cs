using System.Net;
using System.Net.Http.Json;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using OpenAgent.Router.Endpoints;
using OpenAgent.Router.Models;
using OpenAgent.Router.Options;
using OpenAgent.Router.Security;
using Xunit;

namespace OpenAgent.Router.Tests.Endpoints;

public class IntentAgentSelectorTests
{
    private static readonly IReadOnlyList<AgentSummary> Candidates =
    [
        new AgentSummary { AgentId = "finance", Name = "Finance" },
        new AgentSummary { AgentId = "support", Name = "Support" }
    ];

    [Fact]
    public void ParseDecision_ValidFencedJson_NormalizesAgentId()
    {
        const string content = """
            ```json
            {"agentId":"FINANCE","confidence":0.91,"reason":"invoice request"}
            ```
            """;

        string? decision = IntentAgentSelector.ParseDecision(content, Candidates, 0.5);

        Assert.NotNull(decision);
        Assert.Equal("finance", decision);
    }

    [Theory]
    [InlineData("{\"agentId\":\"unknown\",\"confidence\":0.9}")]
    [InlineData("{\"agentId\":\"finance\",\"confidence\":0.2}")]
    [InlineData("{\"agentId\":\"finance\",\"confidence\":1.1}")]
    [InlineData("")]
    public void ParseDecision_InvalidSelection_ReturnsNull(string content)
    {
        string? decision = IntentAgentSelector.ParseDecision(content, Candidates, 0.5);

        Assert.Null(decision);
    }

    [Fact]
    public async Task SelectAsync_LoadsVisibleCandidatesAndCallsConfiguredIntentAgent()
    {
        var handler = new RecordingHandler();
        var selector = new IntentAgentSelector(
            new HttpClient(handler),
            new AllowAllVisibilityService(),
            Microsoft.Extensions.Options.Options.Create(new IntentRecognitionOptions
            {
                AgentId = "intent-router",
                MinimumConfidence = 0.5,
                TimeoutMs = 5000
            }));

        string? decision = await selector.SelectAsync(
            new AgentSelectionRequest(
                "find my invoice",
                "http://engine",
                null,
                null,
                "Bearer token",
                "tenant-1",
                new AgentUserContext
                {
                    UserId = "user-1",
                    TenantId = "tenant-1"
                }),
            CancellationToken.None);

        Assert.NotNull(decision);
        Assert.Equal("finance", decision);
        Assert.Equal(
            ["http://engine/api/v1/agent/agents", "http://engine/api/v1/agent/chat"],
            handler.RequestUris);
        Assert.Equal("Bearer token", handler.Authorization);
        Assert.Equal("tenant-1", handler.TenantId);
        Assert.False(handler.HasAgentAudience);
        Assert.Contains("intent-router", handler.RequestBody, StringComparison.Ordinal);
        Assert.Contains("Handles invoices", handler.RequestBody, StringComparison.Ordinal);
        Assert.Contains("find my invoice", handler.RequestBody, StringComparison.Ordinal);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> RequestUris { get; } = [];
        public string? Authorization { get; private set; }
        public string? TenantId { get; private set; }
        public bool HasAgentAudience { get; private set; }
        public string RequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri?.ToString() ?? string.Empty);
            Authorization = request.Headers.GetValues("Authorization").Single();
            TenantId = request.Headers.GetValues("X-Tenant-Id").Single();
            HasAgentAudience |= request.Headers.Contains("X-Agent-Audience");
            if (request.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new List<AgentSummary>
                    {
                        new()
                        {
                            AgentId = "finance",
                            Name = "Finance",
                            Description = "Handles invoices"
                        }
                    })
                };
            }

            RequestBody = request.Content == null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new ChatResponse
                {
                    Message = "{\"agentId\":\"finance\",\"confidence\":0.95}"
                })
            };
        }
    }

    private sealed class AllowAllVisibilityService : IAgentVisibilityService
    {
        public Task<bool> IsAgentVisibleToUserAsync(
            string agentId,
            IAgentUserContext userContext,
            CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<List<string>> GetPublishedAgentIdsAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(new List<string>());

        public Task<string?> GetAgentConfigAsync(
            string agentId,
            CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }
}
