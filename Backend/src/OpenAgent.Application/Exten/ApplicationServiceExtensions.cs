using OpenAgent.Capabilities.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.DependencyInjection;
using OpenAgent.Application.Extensions;

namespace OpenAgent.Application.Exten;

public static class ApplicationServiceExtensions
{
    public static IServiceCollection AddAgentApplication(this IServiceCollection services, IConfiguration configuration)
    {
        // Repeated calls must not append Options bindings or collection values.
        if (services.Any(descriptor => descriptor.ServiceType == typeof(ApplicationRegistration)))
        {
            return services;
        }
        services.AddSingleton(new ApplicationRegistration());
        services.TryAddSingleton<IConfiguration>(configuration);
        services.AddHttpContextAccessor();
        return services
            .AddSecurityServices(configuration)
            .AddModelProviderServices(configuration)
            .AddToolingServices(configuration)
            .AddBuiltinCapabilityServices()
            .AddConversationServices(configuration)
            .AddFileAssetServices(configuration)
            .AddMcpServices()
            .AddSkillServices()
            .AddRagServices()
            .AddRuntimeServices(configuration);
    }

    private sealed class ApplicationRegistration;
}
