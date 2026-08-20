using System.Reflection;

namespace OpenAgent.Contracts.Capabilities;

public sealed class ReflectionFunctionOptions
{
    public bool Enabled { get; set; }

    public ICollection<Assembly> Assemblies { get; } = [];
}

public interface IReflectionFunctionProvider
{
    IReadOnlyList<ReflectionFunctionDescriptor> Discover();
}

public interface IReflectionFunctionRegistry
{
    IReadOnlyList<ReflectionFunctionDescriptor> Functions { get; }
}

public sealed record ReflectionFunctionDescriptor(
    string Name,
    string AssemblyName,
    string DeclaringType,
    string ReturnType,
    bool IsStatic,
    string Signature,
    IReadOnlyList<ReflectionFunctionParameterDescriptor> Parameters,
    IReadOnlyDictionary<string, object?> Metadata);

public sealed record ReflectionFunctionParameterDescriptor(
    string Name,
    string Type,
    int Position,
    bool IsOptional,
    IReadOnlyDictionary<string, object?> Metadata);
