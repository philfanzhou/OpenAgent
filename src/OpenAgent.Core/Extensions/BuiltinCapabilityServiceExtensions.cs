using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.DependencyInjection;
using OpenAgent.Core.Capabilities.Code;
using OpenAgent.Core.Capabilities.Plan;
using OpenAgent.Core.Capabilities.UserProfile;
using OpenAgent.Core.Capabilities.Workspace;
using OpenAgent.Core.Tooling.Abstractions;

namespace OpenAgent.Core.Extensions;

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
