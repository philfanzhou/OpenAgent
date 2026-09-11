using Microsoft.Extensions.Options;

namespace OpenAgent.Runner;

/// <summary>Removes abandoned request inputs after a Runner crash.</summary>
internal sealed class WorkspaceReaper(IOptions<RunnerOptions> options, ILogger<WorkspaceReaper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (Directory.Exists(options.Value.WorkspaceRoot))
                {
                    foreach (string directory in Directory.EnumerateDirectories(options.Value.WorkspaceRoot))
                    {
                        if (Guid.TryParseExact(Path.GetFileName(directory), "N", out _)
                            && new DirectoryInfo(directory).LinkTarget == null
                            && Directory.GetCreationTimeUtc(directory) < DateTime.UtcNow.AddHours(-1))
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
