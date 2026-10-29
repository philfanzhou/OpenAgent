using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Runtime;
using OpenAgent.Runtime.Acp.Client;
using OpenAgent.Runtime.Acp.Processes;
using OpenAgent.Runtime.Acp.Protocol;

namespace OpenAgent.Runtime.OpenCode;

/// <summary>独占的进程与工作区；创建、复用和销毁始终使用同一个生命周期。</summary>
internal sealed class OpenCodeAgentProcess(
    AcpProcess process, AcpClient client, InitializeResponse initialized, string key, string home)
{
    internal AcpProcess Process { get; } = process;
    internal AcpClient Client { get; } = client;
    internal InitializeResponse Initialized { get; } = initialized;
    internal string Key { get; } = key;
    internal string Owner { get; } = Path.GetDirectoryName(home)!;
    internal string Home { get; } = home;
    internal string Workspace { get; } = Path.Combine(home, "workspace");
    internal DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
    internal DateTimeOffset IdleSince;
    internal DateTimeOffset IdleDeadline;
    internal int Sessions;
    internal string? SessionId;
    internal string? HistoryCheckpoint;
    internal string? ToolSchema;
    internal bool IsAlive => !Process.Exit.IsCompleted && !Client.Connection.Completion.IsCompleted;
}

/// <summary>每个会话保留一个空闲进程；池满时回收最久未使用者，工作区随进程回收。</summary>
internal sealed class OpenCodeProcessKeeper : IAsyncDisposable
{
    private readonly OpenCodeOptions _options;
    private readonly ILogger<OpenCodeProcessKeeper> _logger;
    private readonly Dictionary<string, OpenCodeAgentProcess> _kept = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _sweep;
    private readonly object _gate = new();
    private int _reused;
    private int _missed;
    private readonly Dictionary<string, long> _retired = new(StringComparer.Ordinal);
    private Task? _dispose;
    private bool _disposed;

    internal int KeptCount { get { lock (_gate) { return _kept.Count; } } }
    internal int ReusedCount { get { lock (_gate) { return _reused; } } }

    // Safe diagnostics: no identity hashes, owner IDs, credentials or paths.
    internal (int Hits, int Misses, int Kept, IReadOnlyDictionary<string, long> Retired, double[] AgesSeconds) Snapshot()
    {
        lock (_gate)
        {
            return (_reused, _missed, _kept.Count, new Dictionary<string, long>(_retired),
                _kept.Values.Select(process => (DateTimeOffset.UtcNow - process.CreatedAt).TotalSeconds).ToArray());
        }
    }

    public OpenCodeProcessKeeper(IOptions<OpenCodeOptions> options, ILogger<OpenCodeProcessKeeper> logger)
    {
        _options = options.Value;
        _logger = logger;
        _sweep = _options.KeepAliveSeconds > 0 ? Task.Run(SweepAsync) : Task.CompletedTask;
    }

    internal async Task<OpenCodeAgentProcess> AcquireAsync(
        string key, string parent, OpenCodeLaunchSpec launch, RuntimeInvocation invocation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        OpenCodeAgentProcess? cached;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _kept.Remove(parent, out cached);
            if (cached is not null && cached.Key == key && cached.IsAlive && DateTimeOffset.UtcNow < cached.IdleDeadline)
            {
                _reused++;
                cached.IdleSince = default;
                _logger.LogDebug("OpenCode keep-alive acquisition: Hit={Hit}, AgeSeconds={AgeSeconds}, Sessions={Sessions}",
                    true, (DateTimeOffset.UtcNow - cached.CreatedAt).TotalSeconds, cached.Sessions);
                return cached;
            }
            _missed++;
        }
        if (cached is not null)
        {
            await RetireAsync(cached, cached.Key != key ? "identity_changed" : !cached.IsAlive ? "dead" : "expired").ConfigureAwait(false);
        }

        string home = Path.Combine(parent, "p-" + Guid.NewGuid().ToString("N"));
        AcpProcess? process = null;
        AcpClient? client = null;
        try
        {
            string workspace = Path.Combine(home, "workspace");
            OpenCodeWorkspace.CreatePrivateDirectory(home);
            OpenCodeWorkspace.CreatePrivateDirectory(workspace);
            // 工作区持有独立副本：平台的 Skill 缓存租约在轮次结束后即可释放。
            OpenCodeSkills.Materialize(workspace, invocation.Skills, ct);
            process = AcpProcess.Start(OpenCodeProcess.Create(launch, invocation, _options.AllowedNativeTools, home));
            client = new AcpClient(process.Transport, options: new()
            {
                RequestTimeout = TimeSpan.FromSeconds(_options.RequestTimeoutSeconds)
            });
            InitializeResponse initialized = await client.InitializeAsync(new("OpenAgent", "1"), ct).ConfigureAwait(false);
            _logger.LogDebug("OpenCode keep-alive acquisition: Hit={Hit}, AgeSeconds={AgeSeconds}, Sessions={Sessions}", false, 0, 0);
            return new OpenCodeAgentProcess(process, client, initialized, key, home);
        }
        catch
        {
            await DestroyAsync(client, process, home).ConfigureAwait(false);
            throw;
        }
    }

    internal async ValueTask ReleaseAsync(OpenCodeAgentProcess process, bool healthy)
    {
        OpenCodeAgentProcess? retired = process;
        string reason;
        lock (_gate)
        {
            reason = _disposed ? "shutdown" : !healthy ? "unhealthy" : !process.IsAlive ? "dead"
                : _options.KeepAliveSeconds <= 0 ? "disabled"
                : process.Sessions >= _options.MaxSessionsPerProcess ? "session_limit" : "replaced_owner";
            if (reason == "replaced_owner")
            {
                _kept.Remove(process.Owner, out retired);
                if (retired is null && _kept.Count >= _options.MaxKeptProcesses)
                {
                    // TTL 相同，最早到期者就是最久未使用者；只淘汰空闲项。
                    retired = _kept.Values.MinBy(entry => entry.IdleDeadline)!;
                    _kept.Remove(retired.Owner);
                    reason = "capacity_lru";
                }
                process.IdleSince = DateTimeOffset.UtcNow;
                process.IdleDeadline = process.IdleSince.AddSeconds(_options.KeepAliveSeconds);
                _kept[process.Owner] = process;
            }
        }
        if (retired is not null)
        {
            await RetireAsync(retired, reason).ConfigureAwait(false);
        }
    }

    private async Task SweepAsync()
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(Math.Clamp(_options.KeepAliveSeconds, 1, 5)));
        try
        {
            while (await timer.WaitForNextTickAsync(_stopping.Token).ConfigureAwait(false))
            {
                OpenCodeAgentProcess[] expired;
                lock (_gate)
                {
                    expired = _kept.Values.Where(process => !process.IsAlive || DateTimeOffset.UtcNow >= process.IdleDeadline).ToArray();
                    foreach (OpenCodeAgentProcess process in expired)
                    {
                        _kept.Remove(process.Owner);
                    }
                }
                foreach (OpenCodeAgentProcess process in expired)
                {
                    await RetireAsync(process, process.IsAlive ? "expired" : "dead").ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
        }
    }

    private async Task RetireAsync(OpenCodeAgentProcess process, string reason)
    {
        lock (_gate)
        {
            _retired[reason] = _retired.GetValueOrDefault(reason) + 1;
        }
        _logger.LogDebug("OpenCode process retired: Reason={Reason}, AgeSeconds={AgeSeconds}, IdleSeconds={IdleSeconds}, Sessions={Sessions}",
            reason, (DateTimeOffset.UtcNow - process.CreatedAt).TotalSeconds,
            process.IdleSince == default ? 0 : (DateTimeOffset.UtcNow - process.IdleSince).TotalSeconds, process.Sessions);
        await DestroyAsync(process.Client, process.Process, process.Home).ConfigureAwait(false);
    }

    private async Task DestroyAsync(AcpClient? client, AcpProcess? process, string home)
    {
        foreach (IAsyncDisposable? resource in new IAsyncDisposable?[] { client, process })
        {
            if (resource is null)
            {
                continue;
            }
            try { await resource.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { OpenCodeLog.CleanupFailure(_logger, resource.GetType().Name, error.GetType().Name); }
        }
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Directory.Exists(home))
                {
                    Directory.Delete(home, recursive: true);
                }
                return;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                if (attempt == 2)
                {
                    OpenCodeLog.CleanupFailure(_logger, "workspace", error.GetType().Name);
                    return;
                }
                await Task.Delay(100).ConfigureAwait(false);
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            return new(_dispose ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        OpenCodeAgentProcess[] remaining;
        lock (_gate)
        {
            _disposed = true;
            remaining = _kept.Values.ToArray();
            _kept.Clear();
        }
        _stopping.Cancel();
        await _sweep.ConfigureAwait(false);
        foreach (OpenCodeAgentProcess process in remaining)
        {
            await RetireAsync(process, "shutdown").ConfigureAwait(false);
        }
        _stopping.Dispose();
    }
}
