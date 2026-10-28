using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.DependencyInjection;
using OpenAgent.Core.Runtime.Agent;
using OpenAgent.Core.Tooling.Discovery;

namespace OpenAgent.Core.Extensions;

internal static class ToolingServiceExtensions
{
    internal static IServiceCollection AddToolingServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AgentExecutionOptions>(configuration.GetSection("AgentExecution"));
        services.TryAddScoped<CapabilityToolFactory>();
        return services;
    }
}
