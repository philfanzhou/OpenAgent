using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Router.Security;

/// <summary>
/// Resolves API keys through an internal Engine request. The Engine owns the
/// PostgreSQL credential store; Router never needs database-specific references.
/// </summary>
internal sealed class EngineApiKeyIdentityResolver(
    IHttpClientFactory httpClientFactory,
    IRouteTable routeTable,
    ILogger<EngineApiKeyIdentityResolver> logger)
    : IThirdPartyApiKeyIdentityResolver
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ThirdPartyApiKeyIdentity?> ResolveAsync(
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        string? engineEndpoint = routeTable.GetTargetEndpoint("chat");
        if (string.IsNullOrWhiteSpace(engineEndpoint)
            || !Uri.TryCreate(
                $"{engineEndpoint.TrimEnd('/')}/api/v1/agent/me",
                UriKind.Absolute,
                out Uri? endpoint))
        {
            return null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        try
        {
            using HttpResponseMessage response = await httpClientFactory
                .CreateClient("ThirdPartyApiKeyEngine")
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadFromJsonAsync<ThirdPartyApiKeyIdentity>(
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(
                exception,
                "API key identity resolution through Engine failed.");
            return null;
        }
    }
}
