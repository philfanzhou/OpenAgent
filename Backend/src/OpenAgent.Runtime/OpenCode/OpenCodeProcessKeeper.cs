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
    internal string Home { get; } = home;
    internal string Workspace { get; } = Path.Combine(home, "workspace");
    internal DateTimeOffset IdleDeadline;
    internal int Sessions;
    internal string? SessionId;
    internal string? HistoryCheckpoint;
    internal string? ToolSchema;
    internal bool IsAlive => !Process.Exit.IsCompleted && !Client.Connection.Completion.IsCompleted;
}

/// <summary>每个复用键最多保留一个空闲进程；工作区随进程回收，轮次不单独管理目录。</summary>
internal sealed class OpenCodeProcessKeeper : IAsyncDisposable
{
    private readonly OpenCodeOptions _options;
    private readonly ILogger<OpenCodeProcessKeeper> _logger;
    private readonly Dictionary<string, OpenCodeAgentProcess> _kept = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _sweep;
    private readonly object _gate = new();
    private int _reused;
    private bool _disposed;

    internal int KeptCount { get { lock (_gate) { return _kept.Count; } } }
    internal int ReusedCount { get { lock (_gate) { return _reused; } } }

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
            _kept.Remove(key, out cached);
            if (cached is not null && cached.IsAlive && DateTimeOffset.UtcNow < cached.IdleDeadline)
            {
                _reused++;
                return cached;
            }
        }
        if (cached is not null)
        {
            await DestroyAsync(cached.Client, cached.Process, cached.Home).ConfigureAwait(false);
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
        lock (_gate)
        {
            if (!_disposed && healthy && process.IsAlive && _options.KeepAliveSeconds > 0
                && process.Sessions < _options.MaxSessionsPerProcess
                && _kept.Count < _options.MaxKeptProcesses && !_kept.ContainsKey(process.Key))
            {
                process.IdleDeadline = DateTimeOffset.UtcNow.AddSeconds(_options.KeepAliveSeconds);
                _kept.Add(process.Key, process);
                return;
            }
        }
        await DestroyAsync(process.Client, process.Process, process.Home).ConfigureAwait(false);
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
                        _kept.Remove(process.Key);
                    }
                }
                foreach (OpenCodeAgentProcess process in expired)
                {
                    await DestroyAsync(process.Client, process.Process, process.Home).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
        }
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

    public async ValueTask DisposeAsync()
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
            await DestroyAsync(process.Client, process.Process, process.Home).ConfigureAwait(false);
        }
        _stopping.Dispose();
    }
}
