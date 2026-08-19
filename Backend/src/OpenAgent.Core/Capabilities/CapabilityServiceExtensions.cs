using Microsoft.Extensions.DependencyInjection;
using OpenAgent.Contracts.Mcp;
using OpenAgent.Contracts.Models;
using OpenAgent.Core.Abstract;
using OpenAgent.Core.Capabilities;
using OpenAgent.Core.Capabilities.Mcp;
using OpenAgent.Core.Capabilities.Rag;
using OpenAgent.Core.Capabilities.Skill;

namespace OpenAgent.Core.Exten;

internal static class CapabilityServiceExtensions
{
    internal static IServiceCollection AddCapabilityServices(this IServiceCollection services)
    {
        services.AddSingleton<IRagRegistry, RagRegistry>();
        services.AddSingleton<ISkillCatalog, SkillCatalog>();
        services.AddSingleton<IMcpRegistry, McpRegistry>();
        services.AddSingleton<McpTransportFactory>();
        services.AddScoped<McpToolFactory>();
        services.AddScoped<AgentSkillsProviderFactory>();
        services.AddScoped<IMcpConnectionTester, McpConnectionTester>();
        services.AddScoped<ICapabilitySource, RagCapabilitySource>();
        services.AddScoped<CapabilityToolFactory>();
        services.AddReflectionFunctions();

        services.AddScoped<IRagService, RagService>();
        services.AddScoped<IRagAdapter, RagFlowAdapter>();
        services.AddScoped<IRagAdapter, QdrantAdapter>();
        return services;
    }
}
