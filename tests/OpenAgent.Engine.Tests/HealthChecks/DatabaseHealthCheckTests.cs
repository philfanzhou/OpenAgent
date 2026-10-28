using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenAgent.Engine.Host.Health;
using OpenAgent.Infrastructure;
using Xunit;

namespace OpenAgent.Engine.Tests.HealthChecks;

public class DatabaseHealthCheckTests
{
    [Fact]
    public async Task CheckHealthAsync_DatabaseReachable_ReturnsHealthy()
    {
        var options = new DbContextOptionsBuilder<OpenAgentDbContext>()
            .UseInMemoryDatabase("health-test")
            .Options;
        var check = new DatabaseHealthCheck(new InMemoryContextFactory(options));

        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task CheckHealthAsync_DatabaseUnreachable_ReturnsUnhealthy()
    {
        var check = new DatabaseHealthCheck(new ThrowingContextFactory());

        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    private sealed class InMemoryContextFactory(DbContextOptions<OpenAgentDbContext> options)
        : IDbContextFactory<OpenAgentDbContext>
    {
        public OpenAgentDbContext CreateDbContext() => new(options);

        public Task<OpenAgentDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
    }

    private sealed class ThrowingContextFactory : IDbContextFactory<OpenAgentDbContext>
    {
        public OpenAgentDbContext CreateDbContext() => throw new InvalidOperationException("no database");

        public Task<OpenAgentDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("no database");
    }
}
