using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Engine.Hosting;
using OpenAgent.Router.Models;
using OpenAgent.Router.Options;
using OpenAgent.Router.Observability;

namespace OpenAgent.Router.Providers;

/// <summary>
/// Intent recognition backed by the Intern-Decision-4B routing service.
/// </summary>
/// <remarks>
/// The service is a structured decision model, not a chat model: it answers
/// "which of these candidates fits" rather than producing prose. It is also
/// stateless, so it holds no agent registry of its own. That shapes the whole
/// design:
/// <list type="bullet">
///   <item>The candidate list travels with every request. It arrives already
///   filtered by <c>IAgentAccessControl</c>, so a user can only ever be routed
///   to an agent they may use — the model cannot widen that set.</item>
///   <item>Descriptions decide routing quality. They are sent verbatim so that
///   editing an agent's description in the platform tunes routing without
///   touching this code or restarting the model.</item>
///   <item>Every failure returns <see langword="null"/>, which lets the router
///   fall back to its configured agent. A routing outage degrades quality; it
///   must never take chat down.</item>
/// </list>
/// </remarks>
internal sealed class InternDecisionProvider : IAgentProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly InternDecisionOptions _options;
    private readonly ILogger<InternDecisionProvider> _logger;
    private readonly HttpClient _httpClient;

    public InternDecisionProvider(
        string providerId,
        IConfigurationSection settings,
        ILogger<InternDecisionProvider> logger,
        HttpMessageHandler? handler = null)
    {
        Id = providerId;
        _logger = logger;
        _options = BindOptions(settings);

        _httpClient = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _httpClient.Timeout = TimeSpan.FromMilliseconds(_options.TimeoutMs);
    }

    public string Id { get; }

    /// <summary>
    /// The decision service publishes no agent catalog: agents live in the
    /// platform, not in the model. Reporting an empty catalog keeps this
    /// provider from contributing candidates of its own.
    /// </summary>
    public Task<AgentProviderCatalog> GetAgentsAsync(
        AgentProviderRequestContext requestContext,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new AgentProviderCatalog([], IsAvailable: true));

    public Task<AgentProviderConversationStatus> ResolveConversationAsync(
        AgentProviderRequestContext requestContext,
        string conversationId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(AgentProviderConversationStatus.NotFound);

    /// <summary>
    /// Forwards nothing: chat conversations are served by the provider that
    /// owns the agent, never by the routing service.
    /// </summary>
    public Task<AgentForwardingTarget?> ResolveForwardingAsync(
        string? action,
        string? tenantId,
        string? conversationId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<AgentForwardingTarget?>(null);

    public ValueTask ConfigureRequestAsync(
        HttpRequestMessage request,
        AgentForwardingTarget target,
        CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    /// <inheritdoc />
    public async Task<IntentRecognitionResult?> RecognizeIntentAsync(
        AgentProviderRequestContext requestContext,
        string intentAgentId,
        IReadOnlyList<AgentSummary> agents,
        string message,
        CancellationToken cancellationToken = default)
    {
        if (!_options.IsConfigured)
        {
            RouterLog.InternDecisionNotConfigured(_logger, Id);
            return null;
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        IReadOnlyDictionary<string, string> candidates = BuildCandidates(agents);
        if (candidates.Count == 0)
        {
            RouterLog.InternDecisionNoCandidates(_logger, Id);
            return null;
        }

        string endpoint = $"{_options.BaseUrl.TrimEnd('/')}{NormalizePath(_options.RoutePath)}";
        var payload = new RouteRequest
        {
            Query = message,
            Agents = candidates,
            Fallback = _options.FallbackAgentId
        };

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Content = new StringContent(
                JsonSerializer.Serialize(payload, JsonOptions),
                Encoding.UTF8,
                "application/json");
            string? apiKey = InternDecisionOptions.ResolveApiKey();
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                request.Headers.TryAddWithoutValidation("X-API-Key", apiKey);
            }

            RouterLog.InternDecisionRequest(_logger, Id, endpoint, candidates.Count);

            using HttpResponseMessage response = await _httpClient
                .SendAsync(request, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // 401 means the key is missing or wrong; 503 means the model is
                // still loading. Both are operational, so they are logged and
                // turned into a fallback rather than surfaced to the caller.
                RouterLog.InternDecisionUnavailable(
                    _logger,
                    Id,
                    (int)response.StatusCode,
                    await ReadSnippetAsync(response, cancellationToken).ConfigureAwait(false));
                return null;
            }

            RouteResponse? result = await ReadRouteResponseAsync(response, cancellationToken)
                .ConfigureAwait(false);
            IntentRecognitionResult? intent = Interpret(result, candidates, intentAgentId);
            RouterLog.InternDecisionResult(
                _logger,
                Id,
                result?.AgentId,
                result?.Confidence ?? -1,
                result?.Action,
                intent?.AgentId);
            return intent;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            RouterLog.InternDecisionTimedOut(_logger, Id, _options.TimeoutMs);
            return null;
        }
        catch (HttpRequestException exception)
        {
            RouterLog.InternDecisionFailed(_logger, Id, exception);
            return null;
        }
        catch (JsonException exception)
        {
            RouterLog.InternDecisionFailed(_logger, Id, exception);
            return null;
        }
    }

    /// <summary>
    /// Maps the service's answer onto the router's selection result.
    /// </summary>
    /// <remarks>
    /// The service grades its own confidence into an <c>action</c>:
    /// <list type="bullet">
    ///   <item><c>execute</c> — confident enough to route straight through;</item>
    ///   <item><c>review</c> — route, but let the target agent ask a clarifying
    ///   question;</item>
    ///   <item><c>fallback</c> — too uncertain, or the fallback was chosen.</item>
    /// </list>
    /// Only <c>execute</c> and <c>review</c> are treated as a decision. On
    /// <c>fallback</c> this returns <see langword="null"/> so the router applies
    /// its own fallback handling, keeping one place that decides what "no
    /// confident match" means.
    /// </remarks>
    internal IntentRecognitionResult? Interpret(
        RouteResponse? response,
        IReadOnlyDictionary<string, string> candidates,
        string intentAgentId)
    {
        if (response is null || string.IsNullOrWhiteSpace(response.AgentId))
        {
            return null;
        }

        if (string.Equals(response.Action, "fallback", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // The router skips the intent agent itself, so a model answer naming it
        // cannot be honoured.
        if (string.Equals(response.AgentId, intentAgentId, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // Never route to something the caller may not use: the candidate list
        // sent to the model is the authorized set, so an answer outside it is
        // either a drifting model or a tampered response.
        if (!candidates.ContainsKey(response.AgentId))
        {
            RouterLog.InternDecisionUnknownAgent(_logger, Id, response.AgentId);
            return null;
        }

        double confidence = double.IsFinite(response.Confidence)
            ? Math.Clamp(response.Confidence, 0d, 1d)
            : 0d;
        return new IntentRecognitionResult(response.AgentId, confidence);
    }

    /// <summary>
    /// Builds the <c>{agent: description}</c> map the service expects.
    /// </summary>
    /// <remarks>
    /// The service rejects blank descriptions and enforces a 62-entry ceiling.
    /// Both are handled here so a misconfigured platform degrades into a
    /// usable request instead of a rejected one.
    /// </remarks>
    internal IReadOnlyDictionary<string, string> BuildCandidates(IReadOnlyList<AgentSummary> agents)
    {
        const int MaxAgents = 62;
        const int MaxDescriptionLength = 2000;

        var candidates = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (AgentSummary agent in agents)
        {
            if (string.IsNullOrWhiteSpace(agent.AgentId) || candidates.Count >= MaxAgents)
            {
                continue;
            }

            string description = string.IsNullOrWhiteSpace(agent.Description)
                ? (string.IsNullOrWhiteSpace(agent.Name) ? _options.DefaultAgentDescription : agent.Name)
                : agent.Description;
            if (description.Length > MaxDescriptionLength)
            {
                description = description[..MaxDescriptionLength];
            }

            candidates[agent.AgentId] = description;
        }

        // The model only chooses from what it is given, so a missing fallback
        // forces it to pick arbitrarily. It is also a hard requirement of the
        // request contract: a fallback outside the candidate set is rejected
        // outright. Inject the entry rather than fail — and if the list is
        // already at the ceiling, drop the last candidate to make room, because
        // losing one candidate degrades routing while rejecting the request
        // disables it entirely.
        if (!string.IsNullOrWhiteSpace(_options.FallbackAgentId)
            && !candidates.ContainsKey(_options.FallbackAgentId))
        {
            if (candidates.Count >= MaxAgents)
            {
                string? last = candidates.Keys.LastOrDefault();
                if (last is not null)
                {
                    candidates.Remove(last);
                }
            }

            candidates[_options.FallbackAgentId] = _options.FallbackDescription;
        }

        return candidates;
    }

    private static async Task<RouteResponse?> ReadRouteResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await using Stream stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        return await JsonSerializer
            .DeserializeAsync<RouteResponse>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<string> ReadSnippetAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            string body = await response.Content
                .ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);
            return body.Length <= 400 ? body : body[..400];
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private InternDecisionOptions BindOptions(IConfigurationSection settings)
    {
        var options = new InternDecisionOptions
        {
            BaseUrl = settings["BaseUrl"] ?? string.Empty,
            TimeoutMs = int.TryParse(settings["TimeoutMs"], out int timeout) && timeout > 0
                ? timeout
                : 5000
        };

        if (settings["RoutePath"] is { Length: > 0 } routePath)
        {
            options.RoutePath = routePath;
        }

        if (settings["HealthPath"] is { Length: > 0 } healthPath)
        {
            options.HealthPath = healthPath;
        }

        if (settings["FallbackAgentId"] is { Length: > 0 } fallback)
        {
            options.FallbackAgentId = fallback;
        }

        if (settings["FallbackDescription"] is { Length: > 0 } fallbackDescription)
        {
            options.FallbackDescription = fallbackDescription;
        }

        if (settings["DefaultAgentDescription"] is { Length: > 0 } defaultDescription)
        {
            options.DefaultAgentDescription = defaultDescription;
        }

        return options;
    }

    private static string NormalizePath(string path) =>
        path.StartsWith('/') ? path : "/" + path;

    /// <summary>Request body for <c>POST /v1/route</c>.</summary>
    internal sealed class RouteRequest
    {
        [JsonPropertyName("query")]
        public string Query { get; init; } = string.Empty;

        [JsonPropertyName("agents")]
        public IReadOnlyDictionary<string, string> Agents { get; init; } =
            new Dictionary<string, string>();

        [JsonPropertyName("fallback")]
        public string Fallback { get; init; } = string.Empty;
    }

    /// <summary>Response body for <c>POST /v1/route</c>.</summary>
    internal sealed class RouteResponse
    {
        [JsonPropertyName("agent")]
        public string? AgentId { get; init; }

        [JsonPropertyName("confidence")]
        public double Confidence { get; init; }

        [JsonPropertyName("action")]
        public string? Action { get; init; }

        [JsonPropertyName("fallback_agent")]
        public string? FallbackAgent { get; init; }

        [JsonPropertyName("probabilities")]
        public IReadOnlyDictionary<string, double>? Probabilities { get; init; }
    }
}