using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.DependencyInjection;
using OpenAgent.Core.Extensions;

namespace OpenAgent.Core.Exten;

public static class CoreServiceExtensions
{
    public static IServiceCollection AddAgentCore(this IServiceCollection services, IConfiguration configuration)
    {
        // Repeated calls must not append Options bindings or collection values.
        if (services.Any(descriptor => descriptor.ServiceType == typeof(CoreRegistration)))
        {
            return services;
        }
        services.AddSingleton(new CoreRegistration());
        services.TryAddSingleton<IConfiguration>(configuration);
        services.AddHttpContextAccessor();
        return services
            .AddSecurityServices(configuration)
            .AddModelProviderServices(configuration)
            .AddRunnerServices(configuration)
            .AddToolingServices(configuration)
            .AddBuiltinCapabilityServices()
            .AddConversationServices(configuration)
            .AddFileAssetServices(configuration)
            .AddMcpServices(configuration)
            .AddSkillServices()
            .AddRagServices()
            .AddRuntimeServices();
    }

    private sealed class CoreRegistration;
}
