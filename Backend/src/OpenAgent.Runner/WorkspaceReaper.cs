using Microsoft.Extensions.Options;

namespace OpenAgent.Runner;

/// <summary>
/// Removes abandoned request inputs after a Runner crash, and reaps
/// conversation workspaces once they have been idle past the configured period.
/// </summary>
internal sealed class WorkspaceReaper(IOptions<RunnerOptions> options, ILogger<WorkspaceReaper> logger) : BackgroundService
{
    private const string SessionWorkspacePrefix = "session-";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (Directory.Exists(options.Value.WorkspaceRoot))
                {
                    TimeSpan sessionIdle = TimeSpan.FromMinutes(Math.Max(1, options.Value.SessionWorkspaceIdleMinutes));
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
                        else if (name.StartsWith(SessionWorkspacePrefix, StringComparison.Ordinal)
                            && Directory.GetLastWriteTimeUtc(directory) < DateTime.UtcNow - sessionIdle)
                        {
                            Directory.Delete(directory, recursive: true);
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
