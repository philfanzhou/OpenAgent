using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Security;
using OpenAgent.Router.Models;
using OpenAgent.Router.Options;
using OpenAgent.Router.Providers;
using OpenAgent.Router.Selection;
using Xunit;

namespace OpenAgent.Router.Tests.Providers;

public class ModelIntentStrategyTests
{
    private static readonly IReadOnlyList<AgentSummary> Candidates =
    [
        new AgentSummary { AgentId = "finance", Name = "Finance", Description = "Handles invoices" },
        new AgentSummary { AgentId = "support", Name = "Support" }
    ];

    private static readonly AgentProviderRequestContext Context = new(
        new AgentUserContext { UserId = "user-1", TenantId = "tenant-1", IsAuthenticated = true });

    [Fact]
    public async Task SelectAsync_EndpointNotConfigured_SkipsWithoutCallingService()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Answer("finance", 0.9));
        ModelIntentStrategy strategy = Create(handler, endpoint: null);

        IntentSelection? selection = await strategy.SelectAsync(
            Context, "find my invoice", Candidates, CancellationToken.None);

        Assert.Null(selection);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task SelectAsync_SendsOneChoiceQuestionWithCandidatesAndAbstainOption()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Answer("finance", 0.9));
        ModelIntentStrategy strategy = Create(handler);

        IntentSelection? selection = await strategy.SelectAsync(
            Context, "find my invoice", Candidates, CancellationToken.None);

        Assert.Equal(new IntentSelection("finance", "model"), selection);
        Assert.Equal("http://decision/v1/decisions", handler.RequestUri);
        Assert.Equal("secret", handler.ApiKey);
        using JsonDocument body = JsonDocument.Parse(handler.Body!);
        Assert.Contains("find my invoice", body.RootElement.GetProperty("state").GetString());
        JsonElement question = body.RootElement.GetProperty("questions").GetProperty("agent");
        Assert.Equal("choice", question.GetProperty("type").GetString());
        JsonElement criteria = question.GetProperty("criteria");
        Assert.Equal("Finance：Handles invoices", criteria.GetProperty("finance").GetString());
        Assert.Equal("Support", criteria.GetProperty("support").GetString());
        Assert.True(criteria.TryGetProperty(ModelIntentStrategy.NoneKey, out _));
    }

    [Theory]
    [InlineData("support", 0.2)]
    [InlineData("unknown-agent", 0.95)]
    [InlineData("__none__", 0.95)]
    public async Task SelectAsync_LowConfidenceUnknownOrAbstained_ReturnsNull(string choice, double confidence)
    {
        ModelIntentStrategy strategy = Create(new StubHandler(HttpStatusCode.OK, Answer(choice, confidence)));

        IntentSelection? selection = await strategy.SelectAsync(
            Context, "hello", Candidates, CancellationToken.None);

        Assert.Null(selection);
    }

    [Fact]
    public async Task SelectAsync_ServiceError_ReturnsNull()
    {
        ModelIntentStrategy strategy = Create(new StubHandler(HttpStatusCode.InternalServerError, "{}"));

        Assert.Null(await strategy.SelectAsync(Context, "hello", Candidates, CancellationToken.None));
    }

    [Fact]
    public async Task SelectAsync_InvalidJson_ReturnsNull()
    {
        ModelIntentStrategy strategy = Create(new StubHandler(HttpStatusCode.OK, "not json"));

        Assert.Null(await strategy.SelectAsync(Context, "hello", Candidates, CancellationToken.None));
    }

    [Fact]
    public async Task SelectAsync_ServiceSlowerThanTimeout_ReturnsNull()
    {
        ModelIntentStrategy strategy = Create(
            new StubHandler(HttpStatusCode.OK, Answer("finance", 0.9), delay: TimeSpan.FromSeconds(5)),
            timeoutMs: 50);

        Assert.Null(await strategy.SelectAsync(Context, "hello", Candidates, CancellationToken.None));
    }

    [Fact]
    public async Task SelectAsync_CallerCancels_Propagates()
    {
        ModelIntentStrategy strategy = Create(
            new StubHandler(HttpStatusCode.OK, Answer("finance", 0.9), delay: TimeSpan.FromSeconds(5)));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            strategy.SelectAsync(Context, "hello", Candidates, cts.Token));
    }

    private static ModelIntentStrategy Create(
        StubHandler handler,
        string? endpoint = "http://decision/v1/decisions",
        int timeoutMs = 3000) =>
        new(
            new HttpClient(handler),
            Microsoft.Extensions.Options.Options.Create(new DecisionModelOptions
            {
                Endpoint = endpoint,
                TimeoutMs = timeoutMs,
                MinimumConfidence = 0.5,
                Headers = new Dictionary<string, string> { ["X-Api-Key"] = "secret" }
            }),
            NullLogger<ModelIntentStrategy>.Instance);

    private static string Answer(string choice, double confidence) =>
        JsonSerializer.Serialize(new
        {
            answers = new Dictionary<string, object>
            {
                ["agent"] = new
                {
                    type = "choice",
                    choice,
                    probabilities = new Dictionary<string, double> { [choice] = confidence },
                    confidence
                }
            }
        });

    private sealed class StubHandler(
        HttpStatusCode status,
        string responseBody,
        TimeSpan? delay = null) : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public string? RequestUri { get; private set; }
        public string? Body { get; private set; }
        public string? ApiKey { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            RequestUri = request.RequestUri?.ToString();
            ApiKey = request.Headers.TryGetValues("X-Api-Key", out IEnumerable<string>? values)
                ? values.SingleOrDefault()
                : null;
            Body = request.Content == null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            if (delay is { } wait)
            {
                await Task.Delay(wait, cancellationToken);
            }

            return new HttpResponseMessage(status)
            {
                Content = new StringContent(responseBody, System.Text.Encoding.UTF8, "application/json")
            };
        }
    }
}
