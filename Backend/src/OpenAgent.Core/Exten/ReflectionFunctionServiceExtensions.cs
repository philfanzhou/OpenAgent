using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenAgent.Contracts.Capabilities;
using OpenAgent.Core.Capabilities.Reflection;

namespace OpenAgent.Core.Exten;

public static class ReflectionFunctionServiceExtensions
{
    public static IServiceCollection AddReflectionFunctions(
        this IServiceCollection services,
        Action<ReflectionFunctionOptions>? configure = null)
    {
        services.AddOptions<ReflectionFunctionOptions>();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IReflectionFunctionProvider,
            AssemblyReflectionFunctionProvider>());
        services.TryAddSingleton<IReflectionFunctionRegistry, ReflectionFunctionRegistry>();
        return services;
    }
}
