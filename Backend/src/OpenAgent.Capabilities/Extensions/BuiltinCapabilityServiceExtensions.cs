using OpenAgent.Contracts.Capabilities;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.DependencyInjection;
using OpenAgent.Capabilities.Plan;
using OpenAgent.Capabilities.UserProfile;

namespace OpenAgent.Capabilities.Extensions;

internal static class BuiltinCapabilityServiceExtensions
{
    internal static IServiceCollection AddBuiltinCapabilityServices(this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ICapabilitySource, UserProfileCapabilitySource>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ICapabilitySource, PlanCapabilitySource>());
        return services;
    }
}
