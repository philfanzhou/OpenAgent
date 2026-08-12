using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace OpenAgent.Persistence;

public sealed class OpenAgentDbContextFactory : IDesignTimeDbContextFactory<OpenAgentDbContext>
{
    public OpenAgentDbContext CreateDbContext(string[] args)
    {
        string connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__OpenAgentDatabase")
            ?? "Host=localhost;Port=5432;Database=openagent;Username=openagent;Password=openagent";
        var options = new DbContextOptionsBuilder<OpenAgentDbContext>();
        options.UseNpgsql(connectionString);
        return new OpenAgentDbContext(options.Options);
    }
}
