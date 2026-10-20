using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.DependencyInjection;
using OpenAgent.Contracts.Mcp;
using OpenAgent.Contracts.Capabilities;
using OpenAgent.Application.Capabilities.Mcp;
using OpenAgent.Application.Tooling.Abstractions;

namespace OpenAgent.Application.Extensions;

internal static class McpServiceExtensions
{
    internal static IServiceCollection AddMcpServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<McpExecutionOptions>(configuration.GetSection("Mcp"));
        services.TryAddSingleton<IMcpRegistry, McpRegistry>();
        services.TryAddSingleton<McpTransportFactory>();
        services.TryAddScoped<IMcpConnectionTester, McpConnectionTester>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IAgentFeatureFactory, McpFeatureFactory>());
        return services;
    }
}
