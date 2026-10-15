using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.DependencyInjection;
using OpenAgent.Application.Runtime.Agent;
using OpenAgent.Application.Tooling.Discovery;

namespace OpenAgent.Application.Extensions;

internal static class ToolingServiceExtensions
{
    internal static IServiceCollection AddToolingServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AgentExecutionOptions>(configuration.GetSection("AgentExecution"));
        services.TryAddScoped<CapabilityToolFactory>();
        return services;
    }
}
