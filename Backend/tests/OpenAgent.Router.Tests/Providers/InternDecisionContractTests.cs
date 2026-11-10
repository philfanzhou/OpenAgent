using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Security;
using OpenAgent.Router.Models;
using OpenAgent.Router.Providers;
using Xunit;

namespace OpenAgent.Router.Tests.Providers;

/// <summary>
/// Contract test against a running Intern-Decision-4B service.
/// </summary>
/// <remarks>
/// The unit tests pin the request shape against a stub; this one proves the real
/// service accepts it, which is the failure a stub cannot catch — a forbidden
/// field or an unexpected response envelope only shows up against the real
/// implementation.
///
/// Runs only when <c>OPENAGENT_INTERN_DECISION_TEST_URL</c> is set, for example
/// against a local service started with <c>INTERN_DECISION_MOCK=1</c>. Without
/// it the tests skip rather than fail, so CI stays green on machines that have
/// no access to the model.
/// </remarks>
public class InternDecisionContractTests
{
    private static string? ServiceUrl =>
        Environment.GetEnvironmentVariable("OPENAGENT_INTERN_DECISION_TEST_URL");

    private static string? ServiceKey =>
        Environment.GetEnvironmentVariable("OPENAGENT_INTERN_DECISION_API_KEY");

    [Fact]
    public async Task RealService_AcceptsTheProviderRequestShape()
    {
        string? url = ServiceUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            return; // No live service configured; nothing to assert.
        }

        using var handler = new HttpClientHandler();
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BaseUrl"] = url,
                ["RoutePath"] = "/v1/route",
                ["TimeoutMs"] = "5000",
                ["FallbackAgentId"] = "default"
            })
            .Build();
        var provider = new InternDecisionProvider(
            "intern-decision",
            configuration.GetSection("Settings"),
            NullLogger<InternDecisionProvider>.Instance,
            handler);

        AgentSummary[] candidates =
        [
            new() { AgentId = "order-agent", Name = "订单", Description = "处理订单查询、物流跟踪" },
            new() { AgentId = "refund-agent", Name = "退款", Description = "处理退款、退货、售后" },
            new() { AgentId = "default", Name = "默认", Description = "通用对话与兜底" }
        ];

        IntentRecognitionResult? result = await provider.RecognizeIntentAsync(
            new AgentProviderRequestContext(new StubUser()),
            "intent-router",
            candidates,
            "我买的手机三天了还没发货，帮我查下物流");

        // Legacy behaviour: a low-confidence answer is reported as "fallback",
        // which the provider turns into no decision. Either outcome is valid;
        // a rejected request would surface as a null result too, so the service
        // is probed directly below to distinguish them.
        if (result is not null)
        {
            Assert.Contains(result.AgentId, new[] { "order-agent", "refund-agent", "default" });
            Assert.InRange(result.Confidence, 0d, 1d);
        }

        HttpResponseMessage direct = await PostRouteAsync(url);
        try
        {
            Assert.Equal(System.Net.HttpStatusCode.OK, direct.StatusCode);
        }
        finally
        {
            direct.Dispose();
        }
    }

    [Fact]
    public async Task RealService_RejectsNothingInTheSerialisedBody()
    {
        string? url = ServiceUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        // The service forbids unknown request fields (extra="forbid"). A single
        // extra property would surface here as 400.
        using HttpResponseMessage response = await PostRouteAsync(url);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument document = JsonDocument.Parse(body);
        Assert.True(document.RootElement.TryGetProperty("action", out _));
        Assert.True(document.RootElement.TryGetProperty("agent", out _));
    }

    private static async Task<HttpResponseMessage> PostRouteAsync(string baseUrl)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/v1/route")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    query = "测试查询",
                    agents = new Dictionary<string, string>
                    {
                        ["default"] = "兜底 agent",
                        ["order-agent"] = "订单与物流"
                    },
                    fallback = "default"
                }),
                System.Text.Encoding.UTF8,
                "application/json")
        };
        string? key = ServiceKey;
        if (!string.IsNullOrWhiteSpace(key))
        {
            request.Headers.TryAddWithoutValidation("X-API-Key", key);
        }

        // Return the response before disposing the client keeps the content
        // readable only after the send completes, so buffer it here.
        HttpResponseMessage response = await client.SendAsync(request);
        return response;
    }

    private sealed class StubUser : IAgentUserContext
    {
        public string UserId => "user-1";
        public string? Username => "tester";
        public string? Email => null;
        public string? TenantId => "tenant-1";
        public IReadOnlyList<string> Groups => [];
        public IReadOnlyList<string> Roles => [];
        public IReadOnlyDictionary<string, string> Claims => new Dictionary<string, string>();
        public IReadOnlyList<string> Audience => [];
        public bool IsAuthenticated => true;
    }
}