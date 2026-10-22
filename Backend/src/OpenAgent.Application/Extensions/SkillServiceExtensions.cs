using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.DependencyInjection;
using OpenAgent.Contracts.Capabilities;
using OpenAgent.Application.Capabilities.Skill;
using OpenAgent.Application.Tooling.Abstractions;

namespace OpenAgent.Application.Extensions;

internal static class SkillServiceExtensions
{
    internal static IServiceCollection AddSkillServices(this IServiceCollection services)
    {
        services.TryAddSingleton<ISkillCatalog, EmptySkillCatalog>();
        services.TryAddSingleton<SkillPackageCache>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IAgentFeatureFactory, AgentSkillsProviderFactory>());
        return services;
    }
}
