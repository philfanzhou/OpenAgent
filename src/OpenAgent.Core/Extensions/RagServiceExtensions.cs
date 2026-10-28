using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.DependencyInjection;
using OpenAgent.Contracts.Models;
using OpenAgent.Core.Abstract;
using OpenAgent.Core.Capabilities.Rag;
using OpenAgent.Core.Tooling.Abstractions;

namespace OpenAgent.Core.Extensions;

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
