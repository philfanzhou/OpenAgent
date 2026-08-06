using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Mcp;

namespace OpenAgent.Core.Capabilities.Mcp;

internal sealed class McpServerClient(ILoggerFactory loggerFactory)
    : IMcpClient, IDisposable, IAsyncDisposable
{
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private IReadOnlyDictionary<string, McpTool> _tools =
        new Dictionary<string, McpTool>(StringComparer.OrdinalIgnoreCase);
    private McpClient? _client;
    private int _disposeState;

    public bool IsConnected => _client is { Completion.IsCompleted: false };

    public async Task ConnectAsync(
        string serverUrl,
        McpServerType type = McpServerType.Http,
        CancellationToken cancellationToken = default)
    {
        await _connectionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsConnected)
            {
                return;
            }

            await DisconnectCoreAsync().ConfigureAwait(false);
            HttpClientTransport? transport = null;
            McpClient? client = null;
            try
            {
                transport = CreateTransport(serverUrl, type);
                client = await McpClient.CreateAsync(
                    transport,
                    new McpClientOptions
                    {
                        ClientInfo = new Implementation
                        {
                            Name = "OpenAgent",
                            Version = "1.0.0"
                        },
                        InitializationTimeout = TimeSpan.FromSeconds(30)
                    },
                    loggerFactory,
                    cancellationToken).ConfigureAwait(false);

                IList<McpClientTool> tools = await client.ListToolsAsync(
                    options: null,
                    cancellationToken).ConfigureAwait(false);
                _tools = tools.ToDictionary(
                    tool => tool.Name,
                    MapTool,
                    StringComparer.OrdinalIgnoreCase);
                _client = client;
            }
            catch
            {
                await DisposeConnectionAsync(client, transport).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _connectionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await DisconnectCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public Task<List<McpTool>> ListToolsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_tools.Values.ToList());
    }

    public async Task<string> CallToolAsync(
        string toolName,
        Dictionary<string, object> arguments,
        CancellationToken cancellationToken = default)
    {
        if (!_tools.ContainsKey(toolName))
        {
            return $"Error: Tool '{toolName}' not found.";
        }

        McpClient? client = _client;
        if (client == null || client.Completion.IsCompleted)
        {
            return "Error: MCP Client not connected.";
        }

        try
        {
            Dictionary<string, object?> sdkArguments = arguments.ToDictionary(
                pair => pair.Key,
                pair => (object?)pair.Value,
                StringComparer.Ordinal);
            CallToolResult result = await client.CallToolAsync(
                toolName,
                sdkArguments,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            string resultText = result.Content
                .OfType<TextContentBlock>()
                .FirstOrDefault()?.Text
                ?? JsonSerializer.Serialize(result, McpJsonUtilities.DefaultOptions);

            if (result.IsError == true)
            {
                return $"Error executing tool {toolName}: {resultText}";
            }

            return resultText;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (McpException exception)
        {
            return $"Error executing tool {toolName}: {exception.Message}";
        }
        catch (TimeoutException)
        {
            return $"Error: Tool {toolName} execution timed out.";
        }
        catch (HttpRequestException exception)
        {
            return $"Error executing tool {toolName}: {exception.Message}";
        }
    }

    public async Task<Stream> ReadResourceAsync(
        string resourceUri,
        CancellationToken cancellationToken = default)
    {
        McpClient? client = _client;
        if (client == null || client.Completion.IsCompleted)
        {
            throw new InvalidOperationException("MCP Client not connected.");
        }

        ReadResourceResult result = await client.ReadResourceAsync(
            resourceUri,
            options: null,
            cancellationToken).ConfigureAwait(false);
        return result.Contents.FirstOrDefault() switch
        {
            TextResourceContents text => new MemoryStream(Encoding.UTF8.GetBytes(text.Text)),
            BlobResourceContents blob => new MemoryStream(blob.DecodedData.ToArray(), writable: false),
            null => throw new InvalidOperationException(
                $"Resource '{resourceUri}' returned no content."),
            _ => throw new InvalidOperationException(
                $"Resource '{resourceUri}' returned an unsupported content type.")
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        await DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        _connectionLock.Dispose();
        GC.SuppressFinalize(this);
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private HttpClientTransport CreateTransport(string serverUrl, McpServerType type)
    {
        string normalizedUrl = serverUrl.Trim().TrimEnd('/');
        (Uri endpoint, HttpTransportMode mode) = type switch
        {
            McpServerType.Http => (
                new Uri(serverUrl.Trim(), UriKind.Absolute),
                HttpTransportMode.StreamableHttp),
            McpServerType.SSE => (
                new Uri(normalizedUrl.EndsWith("/sse", StringComparison.OrdinalIgnoreCase)
                    ? normalizedUrl
                    : $"{normalizedUrl}/sse", UriKind.Absolute),
                HttpTransportMode.Sse),
            _ => throw new NotSupportedException(
                $"MCP server type '{type}' is not supported by the HTTP client transport.")
        };
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
            loggerFactory);
    }

    private async Task DisconnectCoreAsync()
    {
        McpClient? client = _client;
        _client = null;
        _tools = new Dictionary<string, McpTool>(StringComparer.OrdinalIgnoreCase);
        if (client != null)
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task DisposeConnectionAsync(
        McpClient? client,
        HttpClientTransport? transport)
    {
        if (client != null)
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
        else if (transport != null)
        {
            await transport.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static McpTool MapTool(McpClientTool tool) => new()
    {
        Name = tool.Name,
        Description = tool.Description,
        Schema = tool.JsonSchema.GetRawText(),
        IsDangerous = tool.ProtocolTool.Annotations?.DestructiveHint == true
    };

}
