using OpenAgent.Router.Observability;

namespace OpenAgent.Router.Routing;

internal sealed class RegistryPoller(
    TimeSpan refreshInterval,
    TimeProvider timeProvider,
    ILogger logger)
{
    internal async Task RunAsync(Func<CancellationToken, Task> refresh, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await refresh(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                RouterLog.RefreshSnapshotFailed(logger, exception);
            }

            try
            {
                await Task.Delay(refreshInterval, timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
