using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.DependencyInjection;
using OpenAgent.Core.Abstract;
using OpenAgent.Core.Capabilities.Skill;
using OpenAgent.Core.Tooling.Abstractions;

namespace OpenAgent.Core.Extensions;

internal static class SkillServiceExtensions
{
    internal static IServiceCollection AddSkillServices(this IServiceCollection services)
    {
        services.TryAddSingleton<ISkillCatalog, SkillCatalog>();
        services.TryAddScoped<AgentSkillsProviderFactory>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IAgentFeatureFactory, SkillFeatureFactory>());
        return services;
    }
}
