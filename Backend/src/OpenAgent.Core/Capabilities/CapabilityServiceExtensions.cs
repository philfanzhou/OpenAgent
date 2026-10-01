using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenAgent.Contracts.Models;
using OpenAgent.Core.Capabilities;
using OpenAgent.Core.Abstract;
using OpenAgent.Core.Capabilities.Mcp;
using OpenAgent.Core.Capabilities.Plan;
using OpenAgent.Core.Capabilities.Rag;
using OpenAgent.Core.Capabilities.Skill;
using OpenAgent.Core.Capabilities.UserProfile;
using OpenAgent.Contracts.Mcp;

namespace OpenAgent.Core.Exten;

internal static class CapabilityServiceExtensions
{
    internal static IServiceCollection AddCapabilityServices(this IServiceCollection services)
    {
        services.TryAddSingleton<IRagRegistry, RagRegistry>();
        services.TryAddSingleton<ISkillCatalog, SkillCatalog>();
        services.TryAddSingleton<IMcpRegistry, McpRegistry>();
        services.TryAddSingleton<McpTransportFactory>();
        services.TryAddSingleton<McpClientPool>();
        services.TryAddScoped<McpToolFactory>();
        services.TryAddScoped<AgentSkillsProviderFactory>();
        services.TryAddScoped<IMcpConnectionTester, McpConnectionTester>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ICapabilitySource, RagCapabilitySource>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ICapabilitySource, UserProfileCapabilitySource>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ICapabilitySource, PlanCapabilitySource>());
        services.TryAddScoped<CapabilityToolFactory>();

        services.TryAddScoped<IRagService, RagService>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IRagAdapter, RagFlowAdapter>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IRagAdapter, QdrantAdapter>());
        return services;
    }
}
