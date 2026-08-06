using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenAgent.Contracts.Configuration;
using SdkMcpClient = ModelContextProtocol.Client.McpClient;

namespace OpenAgent.Core.Capabilities.Mcp;

internal sealed class McpConnection
{
    private readonly McpSessionState _state;
    private readonly McpToolCatalog _catalog;
    private readonly McpTransportFactory _transportFactory;
    private readonly ILogger<McpClient> _logger;
    private readonly ILoggerFactory _loggerFactory;

    internal McpConnection(
        McpSessionState state,
        McpToolCatalog catalog,
        McpTransportFactory transportFactory,
        ILogger<McpClient> logger,
        ILoggerFactory loggerFactory)
    {
        _state = state;
        _catalog = catalog;
        _transportFactory = transportFactory;
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    internal async Task ConnectAsync(
        McpServerConfig server,
        CancellationToken cancellationToken)
    {
        await _state.ConnectionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state.IsConnected)
            {
                return;
            }

            await DisconnectCoreAsync().ConfigureAwait(false);

            string normalizedServerUrl = server.Url?.Trim() ?? string.Empty;
            IClientTransport? transport = null;
            SdkMcpClient? client = null;
            try
            {
                transport = _transportFactory.Create(server);
                client = await SdkMcpClient.CreateAsync(
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
                    _loggerFactory,
                    cancellationToken).ConfigureAwait(false);

                IList<McpClientTool> tools = await client.ListToolsAsync(
                    options: null,
                    cancellationToken).ConfigureAwait(false);
                _catalog.Replace(tools);
                _state.Activate(client, string.IsNullOrWhiteSpace(server.Name) ? normalizedServerUrl : server.Name);
            }
            catch (OperationCanceledException)
            {
                await DisposeConnectionAsync(client, transport).ConfigureAwait(false);
                throw;
            }
            catch (Exception exception)
            {
                await DisposeConnectionAsync(client, transport).ConfigureAwait(false);
                McpLog.ConnectFailed(_logger, exception);
                throw new ConnectionException(
                    $"Failed to connect to MCP server: {exception.Message}",
                    exception);
            }
        }
        finally
        {
            _state.ConnectionLock.Release();
        }
    }

    internal async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        await _state.ConnectionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await DisconnectCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _state.ConnectionLock.Release();
        }
    }

    private async Task DisconnectCoreAsync()
    {
        SdkMcpClient? client = _state.Detach();
        _catalog.Clear();
        if (client != null)
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task DisposeConnectionAsync(
        SdkMcpClient? client,
        IClientTransport? transport)
    {
        if (client != null)
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
        else if (transport != null)
        {
            if (transport is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            }
            else if (transport is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }
}
