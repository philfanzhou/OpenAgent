using System.Diagnostics;
using System.Text.Json;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Router.Models;

namespace OpenAgent.Router.Providers;

/// <summary>
/// Adapts the Gina agent-list and streaming chat protocol to the Router provider contract.
/// </summary>
internal sealed class GinaProvider : IAgentProvider, IDisposable
{
    private readonly string _agentListPath;
    private readonly string _chatPath;
    private readonly string? _defaultAgentId;
    private readonly string? _baseUrl;
    private readonly string? _serverToken;
    private readonly IReadOnlyDictionary<string, string> _serviceHeaders;
    private readonly HttpMessageInvoker _httpClient;

    internal GinaProvider(
        string id,
        IConfiguration settings,
        HttpMessageHandler? handler = null)
    {
        Id = id;
        _agentListPath = NormalizePath(settings["AgentListPath"], "/api/agentlist");
        _chatPath = NormalizePath(settings["ChatPath"], "/api/chat");
        _defaultAgentId = FirstNonEmpty(settings["DefaultAgentId"]);
        _baseUrl = string.IsNullOrWhiteSpace(settings["BaseUrl"])
            ? null
            : settings["BaseUrl"]!.TrimEnd('/');
        _serverToken = FirstNonEmpty(settings["ServerToken"], settings["Token"]);
        _serviceHeaders = settings.GetSection("ServiceHeaders")
            .GetChildren()
            .Where(header => header.Value != null)
            .ToDictionary(
                header => header.Key,
                header => header.Value!,
                StringComparer.OrdinalIgnoreCase);
        _httpClient = new HttpMessageInvoker(handler ?? CreateHandler());
    }

    public string Id { get; }

    public async Task<AgentProviderCatalog> GetAgentsAsync(
        AgentProviderRequestContext requestContext,
        CancellationToken cancellationToken)
    {
        string? endpoint = ResolveEndpoint();
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return new AgentProviderCatalog([], false);
        }

        using HttpRequestMessage request = CreateServiceRequest(
            HttpMethod.Get,
            $"{endpoint.TrimEnd('/')}{_agentListPath}",
            requestContext);
        using HttpResponseMessage response = await _httpClient.SendAsync(
            request,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return new AgentProviderCatalog([], false);
        }

        using JsonDocument document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return new AgentProviderCatalog(ParseAgents(document.RootElement));
    }

    public Task<AgentProviderConversationStatus> ResolveConversationAsync(
        AgentProviderRequestContext requestContext,
        string conversationId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Gina exposes no conversation-owner probe. Do not claim ownership for an opaque ID.
        return Task.FromResult(AgentProviderConversationStatus.NotFound);
    }

    public Task<IntentRecognitionResult?> RecognizeIntentAsync(
        AgentProviderRequestContext requestContext,
        string intentAgentId,
        IReadOnlyList<AgentSummary> agents,
        string message,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Gina's public contract has no intent endpoint. A configured default (or a
        // single published agent) is deterministic routing, not model-based intent recognition.
        AgentSummary? selected = !string.IsNullOrWhiteSpace(_defaultAgentId)
            ? agents.FirstOrDefault(agent => string.Equals(
                agent.AgentId,
                _defaultAgentId,
                StringComparison.OrdinalIgnoreCase))
            : agents.Count == 1
                ? agents[0]
                : null;
        return Task.FromResult<IntentRecognitionResult?>(selected == null
            ? null
            : new IntentRecognitionResult(selected.AgentId, 1));
    }

    public Task<AgentForwardingTarget?> ResolveForwardingAsync(
        string? action,
        string? tenantId,
        string? conversationId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? endpoint = ResolveEndpoint();
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return Task.FromResult<AgentForwardingTarget?>(null);
        }

        // Gina has one chat endpoint; Router stream/sse actions are transport details.
        string targetUrl = $"{endpoint.TrimEnd('/')}{_chatPath}";
        return Task.FromResult<AgentForwardingTarget?>(new(
            endpoint.TrimEnd('/'),
            new Uri(targetUrl)));
    }

    public ValueTask ConfigureRequestAsync(
        HttpRequestMessage request,
        AgentForwardingTarget target,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ApplyServiceHeaders(request);

        string? agentId = GetHeader(request, "X-Agent-Id");
        if (!string.IsNullOrWhiteSpace(agentId))
        {
            ReplaceHeader(request, "X-Gina-Agent-Id", agentId);
        }

        if (string.IsNullOrWhiteSpace(GetHeader(request, "X-Gina-Session-Id")))
        {
            string? conversationId = GetHeader(request, "X-Conversation-Id");
            if (!string.IsNullOrWhiteSpace(conversationId))
            {
                ReplaceHeader(request, "X-Gina-Session-Id", conversationId);
            }
        }

        if (!_serviceHeaders.Keys.Any(IsAuthorizationHeader)
            && !string.IsNullOrWhiteSpace(_serverToken))
        {
            ReplaceHeader(request, "Authorization", FormatBearerToken(_serverToken));
        }

        return ValueTask.CompletedTask;
    }

    public void Dispose() => _httpClient.Dispose();

    private static SocketsHttpHandler CreateHandler() => new()
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        AutomaticDecompression = System.Net.DecompressionMethods.None,
        UseCookies = false,
        EnableMultipleHttp2Connections = true,
        ActivityHeadersPropagator = DistributedContextPropagator.Current,
        ConnectTimeout = TimeSpan.FromSeconds(15)
    };

    private HttpRequestMessage CreateServiceRequest(
        HttpMethod method,
        string url,
        AgentProviderRequestContext? requestContext)
    {
        HttpRequestMessage request = new(method, url);
        ApplyServiceHeaders(request);
        if (!_serviceHeaders.Keys.Any(IsAuthorizationHeader)
            && !string.IsNullOrWhiteSpace(_serverToken))
        {
            ReplaceHeader(request, "Authorization", FormatBearerToken(_serverToken));
        }
        else if (!_serviceHeaders.Keys.Any(IsAuthorizationHeader)
            && !string.IsNullOrWhiteSpace(requestContext?.AuthenticationToken))
        {
            ReplaceHeader(request, "Authorization", requestContext.AuthenticationToken);
        }

        return request;
    }

    private void ApplyServiceHeaders(HttpRequestMessage request)
    {
        foreach ((string name, string value) in _serviceHeaders)
        {
            request.Headers.Remove(name);
            request.Headers.TryAddWithoutValidation(name, value);
        }
    }

    private string? ResolveEndpoint() => _baseUrl;

    private static IReadOnlyList<AgentSummary> ParseAgents(JsonElement root)
    {
        JsonElement agents = root;
        if (root.ValueKind == JsonValueKind.Object)
        {
            agents = FindArray(root, "agents", "agentlist", "agent_list", "items", "data");
        }

        if (agents.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Gina agentlist response must contain an agent array.");
        }

        List<AgentSummary> result = [];
        foreach (JsonElement item in agents.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                string? id = item.GetString();
                if (!string.IsNullOrWhiteSpace(id))
                {
                    result.Add(new AgentSummary { AgentId = id, Name = id });
                }

                continue;
            }

            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? agentId = GetString(item, "agentId", "agent_id", "id", "key");
            if (string.IsNullOrWhiteSpace(agentId))
            {
                continue;
            }

            result.Add(new AgentSummary
            {
                AgentId = agentId,
                Name = GetString(item, "name", "agentName", "agent_name", "displayName", "display_name")
                    ?? agentId,
                Description = GetString(item, "description", "agentDescription", "agent_description", "desc")
                    ?? string.Empty
            });
        }

        return result;
    }

    private static JsonElement FindArray(JsonElement root, params string[] names)
    {
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (names.Any(name => string.Equals(
                    name,
                    property.Name,
                    StringComparison.OrdinalIgnoreCase))
                && property.Value.ValueKind == JsonValueKind.Array)
            {
                return property.Value;
            }
        }

        return default;
    }

    private static string? GetString(JsonElement element, params string[] names)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!names.Any(name => string.Equals(
                    name,
                    property.Name,
                    StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            return property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString()
                : null;
        }

        return null;
    }

    private static string? GetHeader(HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out IEnumerable<string>? values)
            ? values.FirstOrDefault()
            : null;

    private static void ReplaceHeader(HttpRequestMessage request, string name, string value)
    {
        request.Headers.Remove(name);
        request.Headers.TryAddWithoutValidation(name, value);
    }

    private static bool IsAuthorizationHeader(string name) =>
        string.Equals(name, "Authorization", StringComparison.OrdinalIgnoreCase);

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static string FormatBearerToken(string token) =>
        token.Contains(' ', StringComparison.Ordinal)
            ? token
            : $"Bearer {token}";

    private static string NormalizePath(string? value, string fallback)
    {
        string path = string.IsNullOrWhiteSpace(value) ? fallback : value;
        return path.StartsWith("/", StringComparison.Ordinal) ? path : $"/{path}";
    }
}
