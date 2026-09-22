using Microsoft.Extensions.Options;

namespace OpenAgent.Runner;

/// <summary>
/// Reaps idle session sandboxes through the manager, removes abandoned request
/// inputs after a Runner crash, and sweeps leftover session directories once they
/// have been idle past the configured period.
/// </summary>
internal sealed class WorkspaceReaper(
    SessionSandboxManager sessions, IOptions<RunnerOptions> options, ILogger<WorkspaceReaper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                TimeSpan sessionIdle = TimeSpan.FromMinutes(Math.Max(1, options.Value.SessionIdleMinutes));
                await sessions.ReapIdleAsync(sessionIdle).ConfigureAwait(false);
                if (Directory.Exists(options.Value.WorkspaceRoot))
                {
                    foreach (string directory in Directory.EnumerateDirectories(options.Value.WorkspaceRoot))
                    {
                        string name = Path.GetFileName(directory);
                        if (new DirectoryInfo(directory).LinkTarget != null)
                        {
                            continue;
                        }
                        if (Guid.TryParseExact(name, "N", out _))
                        {
                            if (Directory.GetCreationTimeUtc(directory) < DateTime.UtcNow.AddHours(-1))
                            {
                                Directory.Delete(directory, recursive: true);
                            }
                        }
                        else if (name.StartsWith(SessionSandboxManager.SessionDirectoryPrefix, StringComparison.Ordinal)
                            && Directory.GetLastWriteTimeUtc(directory) < DateTime.UtcNow - sessionIdle
                            && !sessions.IsLive(name[SessionSandboxManager.SessionDirectoryPrefix.Length..]))
                        {
                            Directory.Delete(directory, recursive: true);
                            // 工作区（/work 宿主状态）随目录一起被清扫：标记该会话，
                            // 下一次执行以 sandboxReset 告知模型此前的文件已丢失。
                            sessions.MarkSwept(name[SessionSandboxManager.SessionDirectoryPrefix.Length..]);
                        }
                    }
                }
            }
            catch (Exception)
            {
                RunnerLog.CleanupFailed(logger, "abandoned-workspaces");
            }
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken).ConfigureAwait(false);
        }
    }
}
