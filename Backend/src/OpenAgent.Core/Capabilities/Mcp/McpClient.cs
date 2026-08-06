using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAgent.Core.Abstract;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Mcp;

namespace OpenAgent.Core.Capabilities.Mcp;

internal sealed class McpClient : IMcpClient, IDisposable, IAsyncDisposable
{
    private readonly McpSessionState _state;
    private readonly McpToolCatalog _catalog;
    private readonly McpConnection _connection;
    private readonly McpToolInvoker _toolInvoker;
    private readonly McpResourceReader _resourceReader;
    private int _disposeState;

    public McpClient(
        IHttpClientFactory httpClientFactory,
        ILogger<McpClient> logger,
        ILoggerFactory loggerFactory,
        IOptions<McpExecutionOptions> options)
    {
        _state = new McpSessionState();
        _catalog = new McpToolCatalog();
        McpTransportFactory transportFactory = new(httpClientFactory, loggerFactory, options);
        _connection = new McpConnection(
            _state,
            _catalog,
            transportFactory,
            logger,
            loggerFactory);
        _toolInvoker = new McpToolInvoker(_state, _catalog, logger);
        _resourceReader = new McpResourceReader(_state, logger);
    }

    public bool IsConnected => _state.IsConnected;

    public Task ConnectAsync(
        string serverUrl,
        McpServerType type = McpServerType.Http,
        CancellationToken cancellationToken = default)
    {
        return _connection.ConnectAsync(
            new McpServerConfig { Url = serverUrl, Type = type },
            cancellationToken);
    }

    public Task ConnectAsync(
        McpServerConfig server,
        CancellationToken cancellationToken = default)
    {
        return _connection.ConnectAsync(server, cancellationToken);
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        return _connection.DisconnectAsync(cancellationToken);
    }

    public Task<List<McpTool>> ListToolsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_catalog.List());
    }

    public Task<string> CallToolAsync(
        string toolName,
        Dictionary<string, object> arguments,
        CancellationToken cancellationToken = default)
    {
        return _toolInvoker.InvokeAsync(toolName, arguments, cancellationToken);
    }

    public Task<Stream> ReadResourceAsync(
        string uri,
        CancellationToken cancellationToken = default)
    {
        return _resourceReader.ReadAsync(uri, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        await _connection.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        _state.ConnectionLock.Dispose();
        GC.SuppressFinalize(this);
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
