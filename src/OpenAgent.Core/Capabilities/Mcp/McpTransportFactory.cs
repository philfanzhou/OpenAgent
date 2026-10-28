using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using OpenAgent.Contracts.Configuration;

namespace OpenAgent.Core.Capabilities.Mcp;

internal sealed class McpTransportFactory(
    IHttpClientFactory httpClientFactory,
    ILoggerFactory loggerFactory)
{
    internal IClientTransport Create(McpServerConfig server)
    {
        (Uri endpoint, HttpTransportMode mode) = ResolveEndpoint(server.Url, server.Type);
        HttpClient httpClient = httpClientFactory.CreateClient();
        httpClient.Timeout = TimeSpan.FromMinutes(5);

        return new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = endpoint,
                Name = "OpenAgent",
                TransportMode = mode,
                ConnectionTimeout = TimeSpan.FromSeconds(5),
                MaxReconnectionAttempts = 5,
                DefaultReconnectionInterval = TimeSpan.FromSeconds(2)
            },
            httpClient,
            loggerFactory,
            ownsHttpClient: true);
    }

    private static (Uri Endpoint, HttpTransportMode Mode) ResolveEndpoint(
        string serverUrl,
        McpServerType type)
    {
        string normalizedUrl = serverUrl.Trim().TrimEnd('/');
        return type switch
        {
            McpServerType.Http => (
                new Uri(serverUrl.Trim(), UriKind.Absolute),
                HttpTransportMode.StreamableHttp),
            McpServerType.SSE => (
                new Uri(AppendEndpoint(normalizedUrl, "/sse"), UriKind.Absolute),
                HttpTransportMode.Sse),
            _ => throw new NotSupportedException(
                $"MCP server type '{type}' is not supported by the HTTP client transport.")
        };
    }

    private static string AppendEndpoint(string serverUrl, string suffix) =>
        serverUrl.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? serverUrl
            : $"{serverUrl}{suffix}";
}
