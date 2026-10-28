using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Execution;

namespace OpenAgent.Runner;

/// <summary>Lifecycle handle the manager owns; real sandboxes are <see cref="SessionSandbox"/>.</summary>
internal interface ISessionSandbox
{
    string SessionKey { get; }

    DateTimeOffset LastUsedUtc { get; }

    bool IsAlive { get; }

    /// <summary>True when the spawn found a leftover session directory, meaning state
    /// from an earlier reclaimed sandbox is gone (reported as SandboxReset).</summary>
    bool Recovered { get; }

    /// <summary>True when the spawn found a pre-existing host work directory:
    /// 会话工作区跨沙箱重建存活（bind-mount 特性），不再视为 reset。</summary>
    bool WorkspacePreserved { get; }

    Task<CodeExecutionResult> ExecuteAsync(CodeExecutionRequest request, CancellationToken cancellationToken);

    ValueTask ReleaseAsync(string reason);
}

/// <summary>
/// Owns the persistent session sandboxes: one per session key, reused across
/// executions, reclaimed after the configured idle period or evicted for capacity
/// (never while an execution is running).
/// </summary>
internal sealed class SessionSandboxManager
{
    internal const string SessionDirectoryPrefix = "session-";
    private const int MaxAcquireAttempts = 3;

    private readonly IOptions<RunnerOptions> _options;
    private readonly ILogger<SessionSandboxManager> _logger;
    private readonly Func<string, Task<ISessionSandbox>> _spawn;
    private readonly ConcurrentDictionary<string, Entry> _sandboxes = new(StringComparer.Ordinal);
    // 工作区被后台清扫删除的会话标记：下一次执行报告 sandboxReset（一次性消费）。
    private readonly ConcurrentDictionary<string, bool> _sweptWorkspaces = new(StringComparer.Ordinal);

    public SessionSandboxManager(BubblewrapProcess bubblewrap, IOptions<RunnerOptions> options,
        ILogger<SessionSandboxManager> logger)
        : this(options, logger, async sessionKey => (ISessionSandbox)await SessionSandbox.SpawnAsync(
            bubblewrap, options.Value, logger, sessionKey, Path.Combine(AppContext.BaseDirectory, "sandbox")).ConfigureAwait(false))
    {
    }

    internal SessionSandboxManager(IOptions<RunnerOptions> options, ILogger<SessionSandboxManager> logger,
        Func<string, Task<ISessionSandbox>> spawn)
    {
        _options = options;
        _logger = logger;
        _spawn = spawn;
    }

    /// <summary>Number of live session sandboxes (entries currently holding one).</summary>
    public int LiveCount => _sandboxes.Values.Count(entry => entry.Sandbox is not null);

    public bool IsLive(string sessionKey) => _sandboxes.ContainsKey(sessionKey);

    internal ISessionSandbox? GetSandbox(string sessionKey) =>
        _sandboxes.TryGetValue(sessionKey, out Entry? entry) ? entry.Sandbox : null;

    public async Task<CodeExecutionResult> ExecuteAsync(string sessionKey, CodeExecutionRequest request,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            Entry entry = await AcquireEntryAsync(sessionKey, attempt == 0, cancellationToken).ConfigureAwait(false);
            bool reset = false;
            // 后台清扫标记在进入执行时一次性消费：无论沙箱是否重建，工作区丢失
            // 都必须让本轮结果可见。
            bool swept = _sweptWorkspaces.TryRemove(sessionKey, out _);
            try
            {
                // The entry may have been replaced (eviction) while this request
                // waited on the old gate; only proceed as the registered owner.
                if (!_sandboxes.TryGetValue(sessionKey, out Entry? current) || !ReferenceEquals(current, entry))
                {
                    if (attempt >= MaxAcquireAttempts - 1)
                    {
                        throw new RunnerBusyException();
                    }
                    continue;
                }
                if (entry.Sandbox is { IsAlive: false } dead)
                {
                    await DiscardSandboxAsync(entry, dead, "dead").ConfigureAwait(false);
                }
                if (entry.Sandbox is null)
                {
                    entry.Sandbox = await SpawnWithCapacityAsync(sessionKey).ConfigureAwait(false);
                    // Reset 的语义是"会话工作区状态丢失"：后台清扫（显式标记）、
                    // 此前有沙箱但工作区目录不在，或遗留目录标记；沙箱进程重建
                    // 本身不再触发——/work 是宿主持久状态，跨重建存活。
                    reset = (entry.HadSandbox && !entry.Sandbox.WorkspacePreserved)
                        || entry.Sandbox.Recovered;
                    entry.HadSandbox = true;
                    if (reset)
                    {
                        RunnerLog.SandboxReset(_logger, sessionKey);
                    }
                }
                try
                {
                    CodeExecutionResult result = await entry.Sandbox.ExecuteAsync(request, cancellationToken)
                        .ConfigureAwait(false);
                    result.SandboxReset = reset || swept;
                    return result;
                }
                catch (SessionSandboxUnavailableException)
                {
                    await DiscardSandboxAsync(entry, entry.Sandbox!, "unavailable").ConfigureAwait(false);
                    if (attempt >= 1)
                    {
                        throw new InvalidOperationException("The session sandbox is unavailable.");
                    }
                    continue;
                }
            }
            finally
            {
                entry.Gate.Release();
            }
        }
    }

    private async Task<Entry> AcquireEntryAsync(string sessionKey, bool firstAttempt, CancellationToken cancellationToken)
    {
        while (true)
        {
            Entry entry = _sandboxes.GetOrAdd(sessionKey, _ => new Entry());
            if (await entry.Gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                return entry;
            }
            // Same-session requests serialize on one sandbox; a second in-flight call is busy.
            if (firstAttempt)
            {
                throw new RunnerBusyException();
            }
            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<ISessionSandbox> SpawnWithCapacityAsync(string sessionKey)
    {
        // Only entries that actually hold a sandbox count; the entry currently
        // spawning (sandbox still null) must never block or evict itself.
        int live = _sandboxes.Values.Count(entry => entry.Sandbox is not null);
        if (live >= _options.Value.MaxSessionSandboxes && !await TryEvictAsync().ConfigureAwait(false))
        {
            throw new RunnerBusyException();
        }
        return await _spawn(sessionKey).ConfigureAwait(false);
    }

    private async Task<bool> TryEvictAsync()
    {
        foreach ((string key, Entry candidate) in _sandboxes
            .Where(pair => pair.Value.Sandbox is not null)
            .OrderBy(pair => pair.Value.Sandbox!.LastUsedUtc)
            .ToList())
        {
            if (!candidate.Gate.Wait(0))
            {
                continue; // Busy executing; eviction must never kill a running request.
            }
            try
            {
                if (!_sandboxes.TryGetValue(key, out Entry? current) || !ReferenceEquals(current, candidate))
                {
                    continue;
                }
                ISessionSandbox victim = candidate.Sandbox!;
                _sandboxes.TryRemove(key, out _);
                await victim.ReleaseAsync("evicted").ConfigureAwait(false);
                RunnerLog.SandboxEvicted(_logger, key, _options.Value.MaxSessionSandboxes);
                return true;
            }
            finally
            {
                candidate.Gate.Release();
            }
        }
        return false;
    }

    /// <summary>标记某会话的工作区已被清扫删除；其下一次执行将报告 SandboxReset。</summary>
    public void MarkSwept(string sessionKey) => _sweptWorkspaces[sessionKey] = true;

    /// <summary>Kills and unregisters every sandbox that has been idle past the cutoff.</summary>
    public async Task ReapIdleAsync(TimeSpan idle)
    {
        foreach ((string key, Entry entry) in _sandboxes.ToList())
        {
            if (entry.Sandbox is not { } sandbox)
            {
                // Tombstone from a failed spawn: drop it so the directory sweep
                // can reclaim its leftover session directory.
                if (entry.Gate.Wait(0))
                {
                    try
                    {
                        if (_sandboxes.TryGetValue(key, out Entry? current) && ReferenceEquals(current, entry))
                        {
                            _sandboxes.TryRemove(key, out _);
                        }
                    }
                    finally
                    {
                        entry.Gate.Release();
                    }
                }
                continue;
            }
            if (DateTimeOffset.UtcNow - sandbox.LastUsedUtc <= idle)
            {
                continue;
            }
            if (!entry.Gate.Wait(0))
            {
                continue;
            }
            try
            {
                if (_sandboxes.TryGetValue(key, out Entry? current) && ReferenceEquals(current, entry)
                    && entry.Sandbox is { } reap && DateTimeOffset.UtcNow - reap.LastUsedUtc > idle)
                {
                    _sandboxes.TryRemove(key, out _);
                    await reap.ReleaseAsync("idle").ConfigureAwait(false);
                }
            }
            finally
            {
                entry.Gate.Release();
            }
        }
    }

    /// <summary>
    /// Drops a dead sandbox from its entry while keeping the entry registered, so the
    /// respawn on the next attempt is recognized as a reset (state was lost).
    /// </summary>
    private static async Task DiscardSandboxAsync(Entry entry, ISessionSandbox sandbox, string reason)
    {
        entry.Sandbox = null;
        await sandbox.ReleaseAsync(reason).ConfigureAwait(false);
    }

    private sealed class Entry
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public ISessionSandbox? Sandbox;
        public bool HadSandbox;
    }
}
