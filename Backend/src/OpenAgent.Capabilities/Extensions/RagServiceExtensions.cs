using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.DependencyInjection;
using OpenAgent.Contracts.Models;
using OpenAgent.Contracts.Capabilities;
using OpenAgent.Capabilities.Rag;

namespace OpenAgent.Capabilities.Extensions;

internal static class RagServiceExtensions
{
    internal static IServiceCollection AddRagServices(this IServiceCollection services)
    {
        services.TryAddSingleton<IRagRegistry, RagRegistry>();
        services.TryAddScoped<IRagService, RagService>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IRagAdapter, RagFlowAdapter>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IRagAdapter, QdrantAdapter>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ICapabilitySource, RagCapabilitySource>());
        return services;
    }
}
