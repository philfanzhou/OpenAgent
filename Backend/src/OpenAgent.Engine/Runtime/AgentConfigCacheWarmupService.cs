using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAgent.Engine.Config;
using OpenAgent.Engine.Observability;

namespace OpenAgent.Engine.Runtime;

internal sealed class AgentConfigCacheWarmupService(
    AgentConfigDatabaseStore store,
    ILogger<AgentConfigCacheWarmupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!store.IsEnabled)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await store.TryWarmupAsync(stoppingToken).ConfigureAwait(false))
                {
                    return;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                EngineLog.AgentConfigCacheWarmupFailed(logger, exception);
            }

            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
        }
    }
}
