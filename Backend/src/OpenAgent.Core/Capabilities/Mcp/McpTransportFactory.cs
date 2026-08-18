using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using OpenAgent.Contracts.Configuration;

namespace OpenAgent.Core.Capabilities.Mcp;

internal sealed class McpTransportFactory(
    IHttpClientFactory httpClientFactory,
    ILoggerFactory loggerFactory,
    IOptions<McpExecutionOptions> options)
{
    private readonly McpExecutionOptions _options = options.Value;

    internal IClientTransport Create(McpServerConfig server)
    {
        if (server.Type == McpServerType.Stdio)
        {
            return CreateStdioTransport(server);
        }

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

    private StdioClientTransport CreateStdioTransport(McpServerConfig server)
    {
        if (!_options.AllowStdio)
        {
            throw new InvalidOperationException(
                "MCP Stdio execution is disabled by the server policy.");
        }
        if (string.IsNullOrWhiteSpace(server.Command))
        {
            throw new InvalidOperationException($"MCP Stdio server '{server.Name}' must specify a command.");
        }

        if (!_options.AllowUnlistedCommands && !IsCommandAllowed(server.Command))
        {
            throw new InvalidOperationException(
                $"MCP Stdio command '{server.Command}' is not allowed by the server policy.");
        }

        Dictionary<string, string?> environment =
            StdioClientTransportOptions.GetDefaultEnvironmentVariables();
        foreach (KeyValuePair<string, string> item in server.EnvironmentVariables)
        {
            if (!_options.AllowedEnvironmentVariables.Contains(
                    item.Key,
                    StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"MCP Stdio environment variable '{item.Key}' is not allowed by the server policy.");
            }
            environment[item.Key] = item.Value;
        }

        string? workingDirectory = ResolveWorkingDirectory(server.WorkingDirectory);

        return new StdioClientTransport(
            new StdioClientTransportOptions
            {
                Command = server.Command,
                Arguments = server.Arguments,
                Name = string.IsNullOrWhiteSpace(server.Name) ? server.Command : server.Name,
                WorkingDirectory = workingDirectory,
                InheritEnvironmentVariables = false,
                EnvironmentVariables = environment,
                ShutdownTimeout = TimeSpan.FromSeconds(5)
            },
            loggerFactory);
    }

    private bool IsCommandAllowed(string command)
    {
        bool containsDirectory = command.Contains(Path.DirectorySeparatorChar)
            || command.Contains(Path.AltDirectorySeparatorChar)
            || command.Contains('/')
            || command.Contains('\\');
        if (!containsDirectory)
        {
            return _options.AllowedCommands.Contains(command, StringComparer.OrdinalIgnoreCase);
        }

        string fullCommand = Path.GetFullPath(command);
        return _options.AllowedCommands
            .Where(Path.IsPathRooted)
            .Select(Path.GetFullPath)
            .Contains(fullCommand, StringComparer.Ordinal);
    }

    private string? ResolveWorkingDirectory(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }

        string workingDirectory = Path.GetFullPath(configured);
        bool allowed = _options.AllowedWorkingDirectories
            .Select(Path.GetFullPath)
            .Any(root => IsWithinRoot(workingDirectory, root));
        if (!allowed)
        {
            throw new InvalidOperationException(
                $"MCP Stdio working directory '{configured}' is not allowed by the server policy.");
        }
        return workingDirectory;
    }

    private static bool IsWithinRoot(string path, string root)
    {
        string normalizedRoot = root.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return string.Equals(path, root, StringComparison.Ordinal)
            || path.StartsWith(normalizedRoot, StringComparison.Ordinal);
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
