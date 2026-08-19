using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Capabilities;

namespace OpenAgent.Core.Capabilities.Reflection;

internal sealed class ReflectionFunctionRegistry : IReflectionFunctionRegistry
{
    private readonly Lazy<IReadOnlyList<ReflectionFunctionDescriptor>> _functions;

    public ReflectionFunctionRegistry(
        IEnumerable<IReflectionFunctionProvider> providers,
        IOptions<ReflectionFunctionOptions> options)
    {
        _functions = new Lazy<IReadOnlyList<ReflectionFunctionDescriptor>>(
            () => Discover(providers, options.Value.Enabled));
    }

    public IReadOnlyList<ReflectionFunctionDescriptor> Functions => _functions.Value;

    private static IReadOnlyList<ReflectionFunctionDescriptor> Discover(
        IEnumerable<IReflectionFunctionProvider> providers,
        bool enabled)
    {
        if (!enabled)
        {
            return [];
        }

        List<ReflectionFunctionDescriptor> functions = [];
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (IReflectionFunctionProvider provider in providers)
        {
            foreach (ReflectionFunctionDescriptor function in provider.Discover())
            {
                if (!names.Add(function.Name))
                {
                    throw new InvalidOperationException(
                        $"Duplicate reflection function name: {function.Name}");
                }

                functions.Add(function);
            }
        }

        return functions.AsReadOnly();
    }
}
