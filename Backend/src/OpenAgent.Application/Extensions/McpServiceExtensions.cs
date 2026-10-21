using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.DependencyInjection;
using OpenAgent.Application.Capabilities.Mcp;
using OpenAgent.Application.Tooling.Abstractions;

namespace OpenAgent.Application.Extensions;

internal static class McpServiceExtensions
{
    internal static IServiceCollection AddMcpServices(this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IAgentFeatureFactory, McpFeatureFactory>());
        return services;
    }
}
