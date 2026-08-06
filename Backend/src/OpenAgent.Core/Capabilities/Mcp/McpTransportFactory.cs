using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using OpenAgent.Contracts.Configuration;

namespace OpenAgent.Core.Capabilities.Mcp;

internal sealed class McpTransportFactory
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly McpExecutionOptions _options;

    internal McpTransportFactory(
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory,
        IOptions<McpExecutionOptions> options)
    {
        _httpClientFactory = httpClientFactory;
        _loggerFactory = loggerFactory;
        _options = options.Value;
    }

    internal IClientTransport Create(McpServerConfig server)
    {
        if (server.Type == McpServerType.Stdio)
        {
            return CreateStdioTransport(server);
        }

        (Uri endpoint, HttpTransportMode mode) = ResolveEndpoint(server.Url, server.Type);
        HttpClient httpClient = _httpClientFactory.CreateClient();
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
            _loggerFactory,
            ownsHttpClient: true);
    }

    private StdioClientTransport CreateStdioTransport(McpServerConfig server)
    {
        if (string.IsNullOrWhiteSpace(server.Command))
        {
            throw new InvalidOperationException($"MCP Stdio server '{server.Name}' must specify a command.");
        }
        if (!_options.AllowUnlistedCommands
            && !_options.AllowedCommands.Contains(
                Path.GetFileName(server.Command), StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"MCP Stdio command '{server.Command}' is not allowed by the server policy.");
        }

        Dictionary<string, string?> environment =
            StdioClientTransportOptions.GetDefaultEnvironmentVariables();
        foreach (var item in server.EnvironmentVariables)
        {
            environment[item.Key] = item.Value;
        }

        return new StdioClientTransport(
            new StdioClientTransportOptions
            {
                Command = server.Command,
                Arguments = server.Arguments,
                Name = string.IsNullOrWhiteSpace(server.Name) ? server.Command : server.Name,
                WorkingDirectory = server.WorkingDirectory,
                InheritEnvironmentVariables = false,
                EnvironmentVariables = environment,
                ShutdownTimeout = TimeSpan.FromSeconds(5)
            },
            _loggerFactory);
    }

    internal static (Uri Endpoint, HttpTransportMode Mode) ResolveEndpoint(
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

    private static string AppendEndpoint(string serverUrl, string suffix)
    {
        return serverUrl.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? serverUrl
            : $"{serverUrl}{suffix}";
    }
}
