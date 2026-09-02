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
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await store.TryWarmupAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                EngineLog.AgentConfigCacheWarmupFailed(logger, exception);
            }

            await Task.Delay(store.ReconciliationInterval, stoppingToken).ConfigureAwait(false);
        }
    }
}
