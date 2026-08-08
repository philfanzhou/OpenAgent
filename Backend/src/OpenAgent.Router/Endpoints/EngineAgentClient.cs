using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using OpenAgent.Hosting.Authorization;
using OpenAgent.Router.Models;

namespace OpenAgent.Router.Endpoints;

internal sealed class EngineAgentClient(HttpClient httpClient) : IEngineAgentClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<IReadOnlyList<AgentSummary>> ListAgentsAsync(
        string engineEndpoint,
        DownstreamRequestIdentity identity,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = CreateRequest(
            HttpMethod.Get,
            engineEndpoint,
            "/api/v1/agent/agents",
            identity);
        using HttpResponseMessage response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<AgentSummary>>(
            JsonOptions,
            cancellationToken).ConfigureAwait(false) ?? [];
    }

    public async Task<string?> ChatAsync(
        string engineEndpoint,
        DownstreamRequestIdentity identity,
        string agentId,
        string message,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = CreateRequest(
            HttpMethod.Post,
            engineEndpoint,
            "/api/v1/agent/chat",
            identity);
        ChatRequest chatRequest = new()
        {
            Message = message,
            Context = new Dictionary<string, object>
            {
                ["agentId"] = agentId
            }
        };
        request.Content = new StringContent(
            JsonSerializer.Serialize(chatRequest, JsonOptions),
            Encoding.UTF8,
            "application/json");
        using HttpResponseMessage response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        ChatResponse? body = await response.Content.ReadFromJsonAsync<ChatResponse>(
            JsonOptions,
            cancellationToken).ConfigureAwait(false);
        return body?.Message;
    }

    private static HttpRequestMessage CreateRequest(
        HttpMethod method,
        string engineEndpoint,
        string path,
        DownstreamRequestIdentity identity)
    {
        HttpRequestMessage request = new(
            method,
            $"{engineEndpoint.TrimEnd('/')}{path}");
        AddHeader(request, GatewayAuthorizationDefaults.GrantHeaderName, identity.GatewayGrant);
        AddHeader(request, "X-Tenant-Id", identity.TenantId);
        AddHeader(request, "X-Trace-Id", identity.TraceId);
        return request;
    }

    private static void AddHeader(
        HttpRequestMessage request,
        string name,
        string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }
    }
}
