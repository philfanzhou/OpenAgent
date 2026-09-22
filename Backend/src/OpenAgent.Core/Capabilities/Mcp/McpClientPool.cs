using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Core.Capabilities.Mcp;

/// <summary>
/// 按（租户, 服务器地址）缓存已连接的 <see cref="McpClient"/>，跨轮次复用，
/// 消掉"每轮 × 每服务器最多 30s 初始化"的延迟大头。
/// 连接失败或 <see cref="InvalidateAsync"/> 后下一轮重连；空闲超过
/// <see cref="McpExecutionOptions.ClientIdleTimeoutSeconds"/> 在下次获取时惰性淘汰。
/// 同一服务器允许跨会话并发使用（StreamableHttp 传输按请求复用 HttpClient）；
/// 单轮内的 MCP 调用仍由执行层独占信号量串行化。
/// </summary>
internal sealed class McpClientPool : IAsyncDisposable
{
    private readonly McpTransportFactory _transportFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<McpClientPool> _logger;
    private readonly TimeSpan _idleTimeout;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public McpClientPool(
        McpTransportFactory transportFactory,
        ILoggerFactory loggerFactory,
        IOptions<McpExecutionOptions> options)
    {
        _transportFactory = transportFactory;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<McpClientPool>();
        int seconds = options.Value.ClientIdleTimeoutSeconds;
        _idleTimeout = seconds <= 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(seconds);
    }

    internal sealed record AcquiredClient(McpClient Client, bool Reconnected);

    /// <summary>
    /// 获取（或建立）指定服务器的连接。每键一把锁串行化连接/淘汰，
    /// 避免并发获取时重复连接；锁只在建连/淘汰时持有，不覆盖工具调用。
    /// </summary>
    internal async Task<AcquiredClient> AcquireAsync(
        McpServerConfig server,
        IAgentUserContext user,
        CancellationToken cancellationToken)
    {
        string key = CreateKey(server, user);
        Entry entry = _entries.GetOrAdd(key, _ => new Entry());
        await entry.Lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            bool reconnected = false;
            if (entry.Client != null
                && DateTimeOffset.UtcNow - entry.LastUsed > _idleTimeout)
            {
                _logger.LogInformation(
                    "Evicting idle MCP client. Server={ServerUrl}", server.Url);
                await DisposeClientAsync(entry.Client).ConfigureAwait(false);
                entry.Client = null;
                reconnected = true;
            }
            if (entry.Client == null)
            {
                entry.Client = await ConnectAsync(server, cancellationToken).ConfigureAwait(false);
                reconnected = true;
            }
            entry.LastUsed = DateTimeOffset.UtcNow;
            return new AcquiredClient(entry.Client, reconnected);
        }
        finally
        {
            entry.Lock.Release();
        }
    }

    /// <summary>调用方发现连接已坏（如 ListTools 抛错）时丢弃缓存，下一轮重连。</summary>
    internal async Task InvalidateAsync(McpServerConfig server, IAgentUserContext user)
    {
        if (!_entries.TryGetValue(CreateKey(server, user), out Entry? entry))
        {
            return;
        }
        await entry.Lock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (entry.Client != null)
            {
                await DisposeClientAsync(entry.Client).ConfigureAwait(false);
                entry.Client = null;
            }
        }
        finally
        {
            entry.Lock.Release();
        }
    }

    /// <summary>强制断开（下一次 Acquire 重连）；供连接测试或运维通道使用。</summary>
    internal Task DisconnectAsync(McpServerConfig server, IAgentUserContext user) =>
        InvalidateAsync(server, user);

    public async ValueTask DisposeAsync()
    {
        foreach (KeyValuePair<string, Entry> pair in _entries)
        {
            await pair.Value.Lock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (pair.Value.Client != null)
                {
                    await DisposeClientAsync(pair.Value.Client).ConfigureAwait(false);
                    pair.Value.Client = null;
                }
            }
            finally
            {
                pair.Value.Lock.Release();
            }
        }
        _entries.Clear();
    }

    private async Task<McpClient> ConnectAsync(McpServerConfig server, CancellationToken cancellationToken)
    {
        IClientTransport transport = _transportFactory.Create(server);
        McpClient? client = null;
        try
        {
            client = await McpClient.CreateAsync(
                transport,
                McpToolFactory.CreateClientOptions(server),
                _loggerFactory,
                cancellationToken).ConfigureAwait(false);
            return client;
        }
        catch
        {
            if (client == null)
            {
                await DisposeTransportAsync(transport).ConfigureAwait(false);
            }
            throw;
        }
    }

    private static async Task DisposeClientAsync(McpClient client)
    {
        try
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // 丢弃坏连接时清理失败无需上抛：连接本身已不可用。
        }
    }

    private static async Task DisposeTransportAsync(IClientTransport transport)
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

    private static string CreateKey(McpServerConfig server, IAgentUserContext user) =>
        $"{user.TenantId ?? string.Empty}|{server.Type}:{server.Url}";

    private sealed class Entry
    {
        public readonly SemaphoreSlim Lock = new(1, 1);
        public McpClient? Client;
        public DateTimeOffset LastUsed;
    }
}
