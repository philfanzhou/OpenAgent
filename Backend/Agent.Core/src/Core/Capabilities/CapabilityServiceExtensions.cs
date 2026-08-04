using Microsoft.Extensions.DependencyInjection;
using OpenAgent.Core.Abstract;
using OpenAgent.Core.Capabilities;
using OpenAgent.Core.Capabilities.Mcp;
using OpenAgent.Core.Capabilities.Rag;
using OpenAgent.Core.Capabilities.Skill;
using OpenAgent.Contracts.Models;
using OpenAgent.Contracts.Skills;

namespace OpenAgent.Core.Exten;

internal static class CapabilityServiceExtensions
{
    internal static IServiceCollection AddCapabilityServices(this IServiceCollection services)
    {
        services.AddSingleton<SkillCatalog>();
        services.AddSingleton<IToolRegistry>(serviceProvider =>
            serviceProvider.GetRequiredService<SkillCatalog>());
        services.AddSingleton<IRagRegistry, RagRegistry>();
        services.AddSingleton<IMcpClientFactory, McpClientFactory>();
        services.AddScoped<McpClientPool>();
        services.AddScoped<ICapabilitySource, McpCapabilitySource>();
        services.AddScoped<ICapabilitySource, SkillCapabilitySource>();
        services.AddScoped<ICapabilitySource, RagCapabilitySource>();
        services.AddScoped<CapabilityToolFactory>();

        services.AddScoped<ISkillProvider, SkillProvider>();
        services.AddScoped<IRagService, RagService>();
        services.AddScoped<RagSearchTool>();
        services.AddScoped<IRagAdapter, RagFlowAdapter>();
        services.AddScoped<IRagAdapter, QdrantAdapter>();
        return services;
    }
}
