using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenAgent.Infrastructure;

namespace OpenAgent.Engine.Host.Health;

internal sealed class DatabaseHealthCheck : IHealthCheck
{
    private readonly IDbContextFactory<OpenAgentDbContext> _contexts;

    public DatabaseHealthCheck(IDbContextFactory<OpenAgentDbContext> contexts)
    {
        _contexts = contexts;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using OpenAgentDbContext database = await _contexts.CreateDbContextAsync(cancellationToken)
                .ConfigureAwait(false);
            bool connected = await database.Database.CanConnectAsync(cancellationToken).ConfigureAwait(false);
            return connected
                ? HealthCheckResult.Healthy("Database is reachable")
                : HealthCheckResult.Unhealthy("Database is not reachable");
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy("Database connection failed", exception);
        }
    }
}
