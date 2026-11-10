using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Security;
using OpenAgent.Router.Options;
using OpenAgent.Router.Models;
using OpenAgent.Router.Providers;
using Xunit;

namespace OpenAgent.Router.Tests.Providers;

/// <summary>
/// The routing service is authoritative for "which agent fits this request",
/// but it must never be able to route somewhere the caller may not go, and it
/// must never take chat down when it fails.
/// </summary>
public class InternDecisionProviderTests
{
    private const string FallbackAgent = "default";

    private static readonly string[] ExpectedCandidateIds = ["order-agent", "refund-agent", FallbackAgent];

    [Fact]
    public async Task RecognizeIntent_SendsAuthorizedCandidatesAndReturnsSelection()
    {
        var handler = new StubHandler(_ =>
            Json("""{"agent":"order-agent","confidence":0.93,"action":"execute"}"""));
        InternDecisionProvider provider = CreateProvider(handler);

        IntentRecognitionResult? result = await provider.RecognizeIntentAsync(
            Context(),
            "intent-router",
            Candidates(),
            "我买的手机三天没发货");

        Assert.NotNull(result);
        Assert.Equal("order-agent", result!.AgentId);
        Assert.Equal(0.93, result.Confidence, 3);

        RouteBody body = handler.LastBody<RouteBody>();
        Assert.Equal("我买的手机三天没发货", body.Query);
        Assert.Equal(FallbackAgent, body.Fallback);
        // Only the candidates given to the provider are forwarded — never a
        // registry the model keeps on its own.
        Assert.Equal(ExpectedCandidateIds.OrderBy(x => x), body.Agents.Keys.OrderBy(x => x));
        Assert.Equal("处理订单、物流查询", body.Agents["order-agent"]);
    }

    [Fact]
    public async Task RecognizeIntent_SendsApiKeyFromEnvironment()
    {
        var handler = new StubHandler(_ =>
            Json("""{"agent":"order-agent","confidence":0.9,"action":"execute"}"""));
        InternDecisionProvider provider = CreateProvider(handler);

        string? key = Environment.GetEnvironmentVariable(InternDecisionOptions.ApiKeyEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(InternDecisionOptions.ApiKeyEnvironmentVariable, "secret-key");

            await provider.RecognizeIntentAsync(Context(), "intent-router", Candidates(), "query");

            Assert.Equal("secret-key", handler.LastRequestHeader("X-API-Key"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(InternDecisionOptions.ApiKeyEnvironmentVariable, key);
        }
    }

    [Fact]
    public async Task RecognizeIntent_OmitsApiKeyHeaderWhenNotConfigured()
    {
        var handler = new StubHandler(_ =>
            Json("""{"agent":"order-agent","confidence":0.9,"action":"execute"}"""));
        InternDecisionProvider provider = CreateProvider(handler);

        string? key = Environment.GetEnvironmentVariable(InternDecisionOptions.ApiKeyEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(InternDecisionOptions.ApiKeyEnvironmentVariable, null);

            await provider.RecognizeIntentAsync(Context(), "intent-router", Candidates(), "query");

            Assert.Null(handler.LastRequestHeader("X-API-Key"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(InternDecisionOptions.ApiKeyEnvironmentVariable, key);
        }
    }

    [Theory]
    [InlineData("execute", "order-agent", 0.93, "order-agent")]
    [InlineData("review", "refund-agent", 0.62, "refund-agent")]
    public async Task RecognizeIntent_AcceptsExecuteAndReview(
        string action,
        string agent,
        double confidence,
        string expected)
    {
        var handler = new StubHandler(_ =>
            Json($$"""{"agent":"{{agent}}","confidence":{{confidence}},"action":"{{action}}"}"""));
        InternDecisionProvider provider = CreateProvider(handler);

        IntentRecognitionResult? result = await provider.RecognizeIntentAsync(
            Context(), "intent-router", Candidates(), "query");

        Assert.NotNull(result);
        Assert.Equal(expected, result!.AgentId);
    }

    [Fact]
    public async Task RecognizeIntent_FallbackActionIsNotADecision()
    {
        // The service grades "too uncertain" as an action of its own. The router
        // owns fallback handling, so this must stay undecided rather than being
        // reported as a confident choice of the fallback agent.
        var handler = new StubHandler(_ =>
            Json("""{"agent":"default","confidence":0.41,"action":"fallback"}"""));
        InternDecisionProvider provider = CreateProvider(handler);

        IntentRecognitionResult? result = await provider.RecognizeIntentAsync(
            Context(), "intent-router", Candidates(), "query");

        Assert.Null(result);
    }

    [Fact]
    public async Task RecognizeIntent_RejectsAgentOutsideTheAuthorizedSet()
    {
        // A drifting model — or a tampered response — must not widen access.
        var handler = new StubHandler(_ =>
            Json("""{"agent":"admin-agent","confidence":0.99,"action":"execute"}"""));
        InternDecisionProvider provider = CreateProvider(handler);

        IntentRecognitionResult? result = await provider.RecognizeIntentAsync(
            Context(), "intent-router", Candidates(), "query");

        Assert.Null(result);
    }

    [Fact]
    public async Task RecognizeIntent_RejectsTheIntentAgentItself()
    {
        var handler = new StubHandler(_ =>
            Json("""{"agent":"intent-router","confidence":0.99,"action":"execute"}"""));
        InternDecisionProvider provider = CreateProvider(handler);
        AgentSummary[] candidates = [.. Candidates(), new AgentSummary
        {
            AgentId = "intent-router",
            Name = "Intent router",
            Description = "Routing helper"
        }];

        IntentRecognitionResult? result = await provider.RecognizeIntentAsync(
            Context(), "intent-router", candidates, "query");

        Assert.Null(result);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task RecognizeIntent_TurnsHttpFailuresIntoFallback(HttpStatusCode status)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(status));
        InternDecisionProvider provider = CreateProvider(handler);

        IntentRecognitionResult? result = await provider.RecognizeIntentAsync(
            Context(), "intent-router", Candidates(), "query");

        Assert.Null(result);
    }

    [Fact]
    public async Task RecognizeIntent_TurnsMalformedJsonIntoFallback()
    {
        var handler = new StubHandler(_ => Json("{not json"));
        InternDecisionProvider provider = CreateProvider(handler);

        IntentRecognitionResult? result = await provider.RecognizeIntentAsync(
            Context(), "intent-router", Candidates(), "query");

        Assert.Null(result);
    }

    [Fact]
    public async Task RecognizeIntent_TurnsTransportFailureIntoFallback()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("connection refused"));
        InternDecisionProvider provider = CreateProvider(handler);

        IntentRecognitionResult? result = await provider.RecognizeIntentAsync(
            Context(), "intent-router", Candidates(), "query");

        Assert.Null(result);
    }

    [Fact]
    public async Task RecognizeIntent_ReturnsNullWhenServiceIsNotConfigured()
    {
        var handler = new StubHandler(_ =>
            Json("""{"agent":"order-agent","confidence":0.9,"action":"execute"}"""));
        InternDecisionProvider provider = CreateProvider(handler, baseUrl: string.Empty);

        IntentRecognitionResult? result = await provider.RecognizeIntentAsync(
            Context(), "intent-router", Candidates(), "query");

        Assert.Null(result);
        // Nothing was sent: an unconfigured provider must not guess an address.
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task RecognizeIntent_ReturnsNullForBlankMessage()
    {
        var handler = new StubHandler(_ =>
            Json("""{"agent":"order-agent","confidence":0.9,"action":"execute"}"""));
        InternDecisionProvider provider = CreateProvider(handler);

        IntentRecognitionResult? result = await provider.RecognizeIntentAsync(
            Context(), "intent-router", Candidates(), "   ");

        Assert.Null(result);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public void BuildCandidates_InjectsTheFallbackAgentWhenMissing()
    {
        InternDecisionProvider provider = CreateProvider(new StubHandler(_ => Json("{}")));
        AgentSummary[] agents =
        [
            new() { AgentId = "order-agent", Name = "Order", Description = "Orders" }
        ];

        IReadOnlyDictionary<string, string> candidates = provider.BuildCandidates(agents);

        // Without a fallback entry the model is forced to pick arbitrarily.
        Assert.True(candidates.ContainsKey(FallbackAgent));
    }

    [Fact]
    public void BuildCandidates_SubstitutesBlankDescriptions()
    {
        InternDecisionProvider provider = CreateProvider(new StubHandler(_ => Json("{}")));
        AgentSummary[] agents =
        [
            new() { AgentId = "no-desc", Name = "Named agent", Description = "   " },
            new() { AgentId = "no-name", Name = "", Description = "" }
        ];

        IReadOnlyDictionary<string, string> candidates = provider.BuildCandidates(agents);

        // The service rejects blank descriptions outright.
        Assert.Equal("Named agent", candidates["no-desc"]);
        Assert.False(string.IsNullOrWhiteSpace(candidates["no-name"]));
    }

    [Fact]
    public void BuildCandidates_RespectsTheSixtyTwoEntryCeiling()
    {
        InternDecisionProvider provider = CreateProvider(new StubHandler(_ => Json("{}")));
        AgentSummary[] agents = Enumerable.Range(0, 100)
            .Select(i => new AgentSummary { AgentId = $"agent-{i:D3}", Description = "desc" })
            .ToArray();

        IReadOnlyDictionary<string, string> candidates = provider.BuildCandidates(agents);

        Assert.True(candidates.Count <= 62, $"expected <= 62, got {candidates.Count}");
    }

    [Fact]
    public void BuildCandidates_SkipsAgentsWithoutId()
    {
        InternDecisionProvider provider = CreateProvider(new StubHandler(_ => Json("{}")));
        AgentSummary[] agents =
        [
            new() { AgentId = "", Description = "no id" },
            new() { AgentId = "valid", Description = "ok" }
        ];

        IReadOnlyDictionary<string, string> candidates = provider.BuildCandidates(agents);

        Assert.DoesNotContain(string.Empty, candidates.Keys);
        Assert.Contains("valid", candidates.Keys);
    }

    // -- helpers ---------------------------------------------------------

    private static InternDecisionProvider CreateProvider(StubHandler handler, string baseUrl = "http://10.51.1.106:8000")
    {
        // The production shape is RouterSettings:AgentProviders:Providers:{i}:Settings,
        // so the section handed to the provider holds the leaf keys directly.
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Settings:BaseUrl"] = baseUrl,
                ["Settings:RoutePath"] = "/v1/route",
                ["Settings:TimeoutMs"] = "5000",
                ["Settings:FallbackAgentId"] = FallbackAgent
            })
            .Build();

        return new InternDecisionProvider(
            "intern-decision",
            configuration.GetSection("Settings"),
            NullLogger<InternDecisionProvider>.Instance,
            handler);
    }

    private static AgentProviderRequestContext Context() =>
        new(new StubUser());

    private static AgentSummary[] Candidates() =>
    [
        new() { AgentId = "order-agent", Name = "订单助手", Description = "处理订单、物流查询" },
        new() { AgentId = "refund-agent", Name = "退款助手", Description = "处理退款、退货、售后维修" },
        new() { AgentId = FallbackAgent, Name = "默认助手", Description = "通用对话" }
    ];

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

    private sealed class StubUser : IAgentUserContext
    {
        public string UserId => "user-1";
        public string? Username => "tester";
        public string? Email => null;
        public string? TenantId => "tenant-1";
        public IReadOnlyList<string> Roles => [];
        public IReadOnlyList<string> Groups => [];
        public IReadOnlyDictionary<string, string> Claims =>
            new Dictionary<string, string>();
        public IReadOnlyList<string> Audience => [];
        public bool IsAuthenticated => true;
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        private readonly List<HttpRequestMessage> _requests = [];
        private readonly List<string> _bodies = [];

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
            _respond = respond;

        public int CallCount => _requests.Count;

        public string? LastRequestHeader(string name) =>
            _requests.Count == 0
                ? null
                : _requests[^1].Headers.TryGetValues(name, out IEnumerable<string>? values)
                    ? values.FirstOrDefault()
                    : null;

        public T LastBody<T>() =>
            JsonSerializer.Deserialize<T>(_bodies[^1], new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            })!;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _requests.Add(request);
            _bodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));
            return _respond(request);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (HttpRequestMessage request in _requests)
                {
                    request.Dispose();
                }
            }

            base.Dispose(disposing);
        }
    }

    private sealed class RouteBody
    {
        public string Query { get; init; } = string.Empty;
        public Dictionary<string, string> Agents { get; init; } = [];
        public string Fallback { get; init; } = string.Empty;
    }
}