using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenAgent.Core.Capabilities.Mcp;
using OpenAgent.Core.Security;

namespace OpenAgent.Core.Exten;

public static class CoreServiceExtensions
{
    public static IServiceCollection AddAgentCore(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.ConfigureHttpClientDefaults(builder =>
        {
            builder.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true
            });
        });
        services.AddHttpContextAccessor();
        services.Configure<McpExecutionOptions>(configuration.GetSection("Mcp"));
        services.Configure<AgentAuthorizationOptions>(configuration.GetSection("Authorization"));

        return services
            .AddConversationServices(configuration)
            .AddCapabilityServices()
            .AddRuntimeServices();
    }
}
