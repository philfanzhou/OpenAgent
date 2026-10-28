using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.DependencyInjection;
using OpenAgent.Contracts.Mcp;
using OpenAgent.Core.Abstract;
using OpenAgent.Core.Capabilities.Mcp;
using OpenAgent.Core.Tooling.Abstractions;

namespace OpenAgent.Core.Extensions;

internal static class McpServiceExtensions
{
    internal static IServiceCollection AddMcpServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<McpExecutionOptions>(configuration.GetSection("Mcp"));
        services.TryAddSingleton<IMcpRegistry, McpRegistry>();
        services.TryAddSingleton<McpTransportFactory>();
        services.TryAddSingleton<McpClientPool>();
        services.TryAddScoped<McpToolFactory>();
        services.TryAddScoped<IMcpConnectionTester, McpConnectionTester>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IAgentFeatureFactory, McpFeatureFactory>());
        return services;
    }
}
