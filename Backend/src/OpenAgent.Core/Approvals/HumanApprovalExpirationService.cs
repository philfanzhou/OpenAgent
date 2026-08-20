using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Approvals;
using OpenAgent.Contracts.Configuration;

namespace OpenAgent.Core.Approvals;

internal sealed partial class HumanApprovalExpirationService(
    IServiceScopeFactory scopeFactory,
    IOptions<HumanApprovalOptions> options,
    TimeProvider timeProvider,
    ILogger<HumanApprovalExpirationService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        TimeSpan interval = TimeSpan.FromSeconds(
            Math.Max(1, options.Value.SweepIntervalSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, timeProvider, stoppingToken).ConfigureAwait(false);
                await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
                IHumanApprovalService approvals = scope.ServiceProvider
                    .GetRequiredService<IHumanApprovalService>();
                await approvals.ExpirePendingAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                ApprovalSweepFailed(logger, exception);
            }
        }
    }

    [LoggerMessage(
        EventId = 1450,
        Level = LogLevel.Error,
        Message = "Human approval expiration sweep failed.")]
    private static partial void ApprovalSweepFailed(
        ILogger logger,
        Exception exception);
}
