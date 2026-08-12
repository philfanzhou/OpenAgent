using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Files;

namespace OpenAgent.Persistence;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddOpenAgentPostgres(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        string connectionString = configuration.GetConnectionString("OpenAgentDatabase")
            ?? throw new InvalidOperationException("ConnectionStrings:OpenAgentDatabase is required.");
        services.AddDbContextFactory<OpenAgentDbContext>(options => options.UseNpgsql(connectionString));
        services.AddSingleton<IConversationStore, PostgresConversationStore>();
        services.AddSingleton<IFileAssetRepository, PostgresFileAssetRepository>();
        return services;
    }
}
