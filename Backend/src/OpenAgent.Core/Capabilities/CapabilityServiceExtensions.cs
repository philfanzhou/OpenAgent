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
        services.AddSingleton<IRagRegistry, RagRegistry>();
        services.AddSingleton<ISkillCatalog, SkillCatalog>();
        services.AddSingleton<IMcpRegistry, McpRegistry>();
        services.AddSingleton<McpTransportFactory>();
        services.AddSingleton<McpClientPool>();
        services.AddScoped<McpToolFactory>();
        services.AddScoped<AgentSkillsProviderFactory>();
        services.AddScoped<IMcpConnectionTester, McpConnectionTester>();
        services.AddScoped<ICapabilitySource, RagCapabilitySource>();
        services.AddScoped<ICapabilitySource, UserProfileCapabilitySource>();
        services.AddScoped<ICapabilitySource, PlanCapabilitySource>();
        services.AddScoped<OpenAgent.Core.Capabilities.Context.RunModelContext>();
        services.AddScoped<ICapabilitySource, OpenAgent.Core.Capabilities.Context.ContextCapabilitySource>();
        services.AddScoped<ICapabilitySource, OpenAgent.Core.Capabilities.Web.WebCapabilitySource>();
        services.TryAddScoped<OpenAgent.Core.Files.FileAssetUrlDownloader>();
        services.AddScoped<OpenAgent.Core.Capabilities.Web.IWebFetcher>(
            provider => new OpenAgent.Core.Capabilities.Web.UrlDownloaderWebFetcher(
                provider.GetRequiredService<OpenAgent.Core.Files.FileAssetUrlDownloader>()));
        services.AddScoped<CapabilityToolFactory>();

        services.AddScoped<IRagService, RagService>();
        services.AddScoped<IRagAdapter, RagFlowAdapter>();
        services.AddScoped<IRagAdapter, QdrantAdapter>();
        return services;
    }
}
