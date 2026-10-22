using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAgent.Runtime.Acp.Client;
using OpenAgent.Runtime.Acp.Processes;
using OpenAgent.Runtime.Acp.Protocol;

namespace OpenAgent.Runtime.OpenCode;

/// <summary>已完成 initialize 的 ACP 进程租约。租约期内由单个轮次独占；主目录随进程销毁回收。工具桥由宿主共享，不随进程持有。</summary>
internal sealed class OpenCodeAgentProcess(
    AcpProcess process,
    AcpClient client,
    InitializeResponse initialized,
    string key,
    string home)
{
    internal AcpProcess Process { get; } = process;
    internal AcpClient Client { get; } = client;
    internal InitializeResponse Initialized { get; } = initialized;
    internal string Key { get; } = key;
    internal string Home { get; } = home;
    internal DateTimeOffset IdleDeadline;
    internal int Sessions;
    internal bool IsAlive => !Process.Exit.IsCompleted && !Client.Connection.Completion.IsCompleted;
}

/// <summary>
/// 空闲进程保活池：追问轮次命中相同会话归属与启动身份的进程即可跳过冷启动
/// （进程启动、initialize、provider 缓存）。失败/取消的进程绝不归还；空闲到期、
/// 会话数或总量超限时销毁。进程不跨会话归属复用。
/// </summary>
internal sealed class OpenCodeProcessKeeper : IAsyncDisposable
{
    private readonly OpenCodeOptions _options;
    private readonly ILogger<OpenCodeProcessKeeper> _logger;
    private readonly Dictionary<string, List<OpenCodeAgentProcess>> _kept = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _sweep;
    private readonly object _gate = new();
    private int _keptCount;
    private int _reused;
    private bool _disposed;

    internal int KeptCount
    {
        get { lock (_gate) { return _keptCount; } }
    }

    internal int ReusedCount
    {
        get { lock (_gate) { return _reused; } }
    }

    public OpenCodeProcessKeeper(IOptions<OpenCodeOptions> options, ILogger<OpenCodeProcessKeeper> logger)
    {
        _options = options.Value;
        _logger = logger;
        _sweep = _options.KeepAliveSeconds > 0 ? Task.Run(SweepAsync) : Task.CompletedTask;
    }

    /// <summary>取出一个可复用进程；不存在、已死亡或已过期时返回 null，由调用方重新启动。</summary>
    internal async Task<OpenCodeAgentProcess?> TryAcquireAsync(string key)
    {
        if (_options.KeepAliveSeconds <= 0)
        {
            return null;
        }
        OpenCodeAgentProcess? acquired = null;
        List<OpenCodeAgentProcess> stale = [];
        lock (_gate)
        {
            if (_disposed || !_kept.TryGetValue(key, out List<OpenCodeAgentProcess>? entries))
            {
                return null;
            }
            while (entries.Count > 0)
            {
                OpenCodeAgentProcess candidate = entries[^1];
                entries.RemoveAt(entries.Count - 1);
                _keptCount--;
                if (candidate.IsAlive && DateTimeOffset.UtcNow < candidate.IdleDeadline)
                {
                    acquired = candidate;
                    _reused++;
                    break;
                }
                stale.Add(candidate);
            }
            if (entries.Count == 0)
            {
                _kept.Remove(key);
            }
        }
        foreach (OpenCodeAgentProcess process in stale)
        {
            await DestroyAsync(process).ConfigureAwait(false);
        }
        return acquired;
    }

    /// <summary>轮次结束后归还进程；仅在轮次完整成功且未超限时空闲保活，其余情况立即销毁。</summary>
    internal async ValueTask ReleaseAsync(OpenCodeAgentProcess process, bool healthy)
    {
        bool keep;
        lock (_gate)
        {
            keep = !_disposed
                && healthy
                && process.IsAlive
                && _options.KeepAliveSeconds > 0
                && process.Sessions < _options.MaxSessionsPerProcess
                && _keptCount < _options.MaxKeptProcesses;
            if (keep)
            {
                process.IdleDeadline = DateTimeOffset.UtcNow.AddSeconds(_options.KeepAliveSeconds);
                if (!_kept.TryGetValue(process.Key, out List<OpenCodeAgentProcess>? entries))
                {
                    _kept[process.Key] = entries = [];
                }
                entries.Add(process);
                _keptCount++;
            }
        }
        if (!keep)
        {
            await DestroyAsync(process).ConfigureAwait(false);
        }
    }

    private async Task SweepAsync()
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(Math.Clamp(_options.KeepAliveSeconds, 1, 5)));
        List<OpenCodeAgentProcess> expired = [];
        try
        {
            while (await timer.WaitForNextTickAsync(_stopping.Token).ConfigureAwait(false))
            {
                List<string> emptied = [];
                expired.Clear();
                lock (_gate)
                {
                    if (_disposed)
                    {
                        break;
                    }
                    foreach (KeyValuePair<string, List<OpenCodeAgentProcess>> pair in _kept)
                    {
                        pair.Value.RemoveAll(process =>
                        {
                            if (process.IsAlive && DateTimeOffset.UtcNow < process.IdleDeadline)
                            {
                                return false;
                            }
                            expired.Add(process);
                            _keptCount--;
                            return true;
                        });
                        if (pair.Value.Count == 0)
                        {
                            emptied.Add(pair.Key);
                        }
                    }
                    foreach (string key in emptied)
                    {
                        _kept.Remove(key);
                    }
                }
                foreach (OpenCodeAgentProcess process in expired)
                {
                    await DestroyAsync(process).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            // 清扫故障不能拖垮宿主；残留条目由下一次获取、释放或销毁兜底回收。
            _logger.LogWarning("OpenCode keep-alive sweep failed ({ErrorType})", error.GetType().Name);
        }
    }

    private async Task DestroyAsync(OpenCodeAgentProcess process)
    {
        try
        {
            await process.Client.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            _logger.LogWarning("OpenCode keep-alive connection disposal failed ({ErrorType})", error.GetType().Name);
        }
        try
        {
            await process.Process.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            _logger.LogWarning("OpenCode keep-alive process disposal failed ({ErrorType})", error.GetType().Name);
        }
        try
        {
            if (Directory.Exists(process.Home))
            {
                Directory.Delete(process.Home, recursive: true);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not remove OpenCode process home {Home} ({ErrorType})", process.Home, error.GetType().Name);
        }
    }

    public async ValueTask DisposeAsync()
    {
        List<OpenCodeAgentProcess> remaining;
        lock (_gate)
        {
            _disposed = true;
            remaining = [.. _kept.Values.SelectMany(entries => entries)];
            _kept.Clear();
            _keptCount = 0;
        }
        _stopping.Cancel();
        await _sweep.ConfigureAwait(false);
        foreach (OpenCodeAgentProcess process in remaining)
        {
            await DestroyAsync(process).ConfigureAwait(false);
        }
        _stopping.Dispose();
    }
}
