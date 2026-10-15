using OpenAgent.Contracts.Capabilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.DependencyInjection;
using OpenAgent.Capabilities.Code;
using OpenAgent.Capabilities.Plan;
using OpenAgent.Capabilities.UserProfile;
using OpenAgent.Capabilities.Workspace;

namespace OpenAgent.Capabilities.Extensions;

internal static class BuiltinCapabilityServiceExtensions
{
    internal static IServiceCollection AddBuiltinCapabilityServices(this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ICapabilitySource, CodeCapabilitySource>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ICapabilitySource, WorkspaceCapabilitySource>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ICapabilitySource, UserProfileCapabilitySource>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ICapabilitySource, PlanCapabilitySource>());
        return services;
    }
}
