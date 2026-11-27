using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OpenAgent.Runtime.OpenCode;

/// <summary>
/// 回收不再使用的原生会话状态：异常退出留下的残留、已失效（无有效检查点）的归属目录，
/// 以及超过保留期的已提交状态。本实例内按进程占用判断；共享存储下另一实例正在执行的轮次持有
/// <c>session.lock</c> 独占租约，探测打不开即跳过。
/// </summary>
internal sealed class OpenCodeSessionStateSweeper : IHostedService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(20);
    /// <summary>无检查点目录的静默期：刚建立或刚失效的归属目录可能正要开始下一轮。</summary>
    private static readonly TimeSpan Quiet = TimeSpan.FromMinutes(5);

    private readonly OpenCodeProcessKeeper _keeper;
    private readonly OpenCodeOptions _options;
    private readonly ILogger<OpenCodeSessionStateSweeper> _logger;
    private CancellationTokenSource? _stopping;
    private Task _loop = Task.CompletedTask;

    /// <summary>当前时间；测试拨快时钟来推进保留期与静默期，不必改文件系统时间。</summary>
    internal Func<DateTime> UtcNow { get; init; } = static () => DateTime.UtcNow;

    public OpenCodeSessionStateSweeper(OpenCodeProcessKeeper keeper, IOptions<OpenCodeOptions> options, ILogger<OpenCodeSessionStateSweeper> logger)
    {
        _keeper = keeper;
        _options = options.Value;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stopping = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_stopping.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping?.Cancel();
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stopping?.IsCancellationRequested == true)
        {
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(StartupDelay, ct).ConfigureAwait(false);
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await SweepOnceAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception error)
                {
                    // 回收不在请求路径上：一次失败只记录，下一周期继续。
                    OpenCodeLog.SessionStateSweepFailed(_logger, error.GetType().Name);
                }
                await Task.Delay(Interval, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// 执行一次回收：扫描两个根目录下的归属目录，删除其中不再被使用且已过期或已失效的。
    /// 单个目录删除失败只记录，不影响其余目录。每轮输出一行结果，便于确认回收器在运行。
    /// </summary>
    internal async Task<int> SweepOnceAsync(CancellationToken ct)
    {
        Stopwatch clock = Stopwatch.StartNew();
        // 非持久目录里的进程最多活到保活窗口或轮次超时；之后仍无进程占用的目录只能是异常退出的残留。
        TimeSpan leftover = TimeSpan.FromSeconds(Math.Max(_options.TurnTimeoutSeconds, _options.KeepAliveSeconds)) + Quiet;
        // 两个根目录嵌套配置时，另一个根目录及其内容不是残留。
        (int committedOwners, int committed) = await SweepRootAsync(
            _options.SessionStateRoot, TimeSpan.FromDays(_options.SessionStateRetentionDays), Quiet, _options.WorkspaceRoot, ct).ConfigureAwait(false);
        (int temporaryOwners, int temporary) = await SweepRootAsync(
            _options.WorkspaceRoot, null, leftover, _options.SessionStateRoot, ct).ConfigureAwait(false);
        OpenCodeLog.SessionStateSweepCompleted(_logger, committedOwners + temporaryOwners, committed + temporary, clock.ElapsedMilliseconds);
        return committed + temporary;
    }

    /// <param name="retention">
    /// 已提交状态自最后一次成功提交起的保留时长，0 表示不按时间回收；null 表示该目录不保存已提交状态。
    /// </param>
    /// <param name="otherRoot">另一个根目录：位于本次扫描范围内的部分不参与回收。</param>
    private async Task<(int Scanned, int Removed)> SweepRootAsync(string root, TimeSpan? retention, TimeSpan quiet, string otherRoot, CancellationToken ct)
    {
        if (!Directory.Exists(root)) return (0, 0);
        int scanned = 0, removed = 0;
        DateTime now = UtcNow();
        foreach (string owner in Directory.EnumerateDirectories(root))
        {
            ct.ThrowIfCancellationRequested();
            if (IsWithin(owner, otherRoot)) continue;
            scanned++;
            // 已提交的状态只在超过保留期后回收；被回收的会话下一轮回放平台历史。
            string manifest = Path.Combine(owner, "committed-session.json");
            bool committed = File.Exists(manifest);
            if (committed && (retention is null || retention == TimeSpan.Zero
                || now - File.GetLastWriteTimeUtc(manifest) < retention)) continue;
            if (!committed && now - LastWrite(owner) < quiet) continue;
            if (IsInUse(owner)) continue;
            for (int attempt = 0; ; attempt++)
            {
                // 二次确认：从判定到删除之间可能刚好有轮次开始使用该目录。
                if (IsInUse(owner)) break;
                Exception? failure = OpenCodeWorkspace.TryDelete(owner);
                if (failure is null)
                {
                    removed++;
                    break;
                }
                if (attempt == 2)
                {
                    OpenCodeLog.CleanupFailure(_logger, "session-state", failure.GetType().Name);
                    break;
                }
                await Task.Delay(100, ct).ConfigureAwait(false);
            }
        }
        return (scanned, removed);
    }

    private bool IsInUse(string owner)
    {
        if (_keeper.IsActive(Path.GetFullPath(owner))) return true;
        string lease = Path.Combine(owner, "session.lock");
        if (!File.Exists(lease)) return false;
        try
        {
            using FileStream probe = new(lease, new FileStreamOptions { Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.None });
            return false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>归属目录及其直接子项的最新写入时间，不递归：用于识别刚建立或刚失效的目录。</summary>
    private static DateTime LastWrite(string owner)
    {
        DateTime latest = Directory.GetLastWriteTimeUtc(owner);
        foreach (string entry in Directory.EnumerateFileSystemEntries(owner))
        {
            DateTime write = File.GetLastWriteTimeUtc(entry);
            if (write > latest) latest = write;
        }
        return latest;
    }

    /// <summary><paramref name="path"/> 是否就是 <paramref name="ancestor"/> 或位于其下。</summary>
    private static bool IsWithin(string ancestor, string path)
    {
        string relative = Path.GetRelativePath(ancestor, path);
        return !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }
}
