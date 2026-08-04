using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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

        return services
            .AddConversationServices(configuration)
            .AddCapabilityServices()
            .AddRuntimeServices();
    }
}
