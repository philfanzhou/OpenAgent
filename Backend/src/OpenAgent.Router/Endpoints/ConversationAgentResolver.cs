using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OpenAgent.Router.Models;

namespace OpenAgent.Router.Endpoints;

internal sealed class ConversationAgentResolver(HttpClient httpClient)
    : IConversationAgentResolver
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<ConversationAgentResolution> ResolveAsync(
        string targetEndpoint,
        string conversationId,
        EngineRequestIdentity identity,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(
            HttpMethod.Get,
            $"{targetEndpoint.TrimEnd('/')}/api/v1/agent/conversations/{Uri.EscapeDataString(conversationId)}/route");
        identity.ApplyTo(request);

        using HttpResponseMessage response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return ConversationAgentResolution.NotFound;
        }

        response.EnsureSuccessStatusCode();
        ConversationRouteResponse? route = await response.Content.ReadFromJsonAsync<ConversationRouteResponse>(
            JsonOptions,
            cancellationToken).ConfigureAwait(false);
        if (route == null || string.IsNullOrWhiteSpace(route.AgentId))
        {
            throw new JsonException("The conversation route response does not contain an Agent ID.");
        }

        return new ConversationAgentResolution(true, route.AgentId);
    }

    private sealed record ConversationRouteResponse(string ConversationId, string? AgentId);
}
