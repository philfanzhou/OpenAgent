using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Capabilities;

namespace OpenAgent.Core.Capabilities.Reflection;

internal sealed class AssemblyReflectionFunctionProvider(
    IOptions<ReflectionFunctionOptions> options) : IReflectionFunctionProvider
{
    private static readonly IReadOnlyDictionary<string, object?> EmptyMetadata =
        new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>());

    private static readonly string? SharedFrameworkPath = GetSharedFrameworkPath();

    private readonly ReflectionFunctionOptions _options = options.Value;

    public IReadOnlyList<ReflectionFunctionDescriptor> Discover()
    {
        if (!_options.Enabled)
        {
            return [];
        }

        List<ReflectionFunctionDescriptor> functions = [];
        foreach (Assembly assembly in _options.Assemblies.Distinct())
        {
            EnsureApplicationAssembly(assembly);
            functions.AddRange(DiscoverAssembly(assembly));
        }

        return functions.AsReadOnly();
    }

    private static IEnumerable<ReflectionFunctionDescriptor> DiscoverAssembly(Assembly assembly)
    {
        foreach (Type type in assembly.GetTypes()
            .Where(type => type.IsVisible)
            .OrderBy(type => type.FullName, StringComparer.Ordinal))
        {
            MethodInfo[] methods = type.GetMethods(
                BindingFlags.Public
                | BindingFlags.Instance
                | BindingFlags.Static
                | BindingFlags.DeclaredOnly);
            foreach (MethodInfo method in methods.OrderBy(method => method.Name, StringComparer.Ordinal))
            {
                if (IsValid(method))
                {
                    yield return Describe(assembly, method);
                }
            }
        }
    }

    private static bool IsValid(MethodInfo method)
    {
        if (method.IsSpecialName
            || method.IsAbstract
            || method.ContainsGenericParameters
            || method.DeclaringType is null
            || method.DeclaringType.ContainsGenericParameters
            || !IsSupportedType(method.ReturnType))
        {
            return false;
        }

        return method.GetParameters().All(parameter =>
            parameter.Name is not null && IsSupportedType(parameter.ParameterType));
    }

    private static bool IsSupportedType(Type type) =>
        !type.IsByRef
        && !type.IsPointer
        && !type.IsByRefLike
        && !type.ContainsGenericParameters;

    private static ReflectionFunctionDescriptor Describe(Assembly assembly, MethodInfo method)
    {
        ParameterInfo[] parameters = method.GetParameters();
        List<ReflectionFunctionParameterDescriptor> parameterDescriptors = parameters
            .Select(parameter => new ReflectionFunctionParameterDescriptor(
                parameter.Name!,
                FormatType(parameter.ParameterType),
                parameter.Position,
                parameter.IsOptional,
                EmptyMetadata))
            .ToList();
        string declaringType = FormatType(method.DeclaringType!);
        string returnType = FormatType(method.ReturnType);
        string arguments = string.Join(", ", parameterDescriptors.Select(
            parameter => $"{parameter.Type} {parameter.Name}"));

        return new ReflectionFunctionDescriptor(
            method.Name,
            assembly.GetName().Name ?? assembly.FullName ?? string.Empty,
            declaringType,
            returnType,
            method.IsStatic,
            $"{returnType} {declaringType}.{method.Name}({arguments})",
            parameterDescriptors.AsReadOnly(),
            EmptyMetadata);
    }

    private static string FormatType(Type type)
    {
        if (type.IsArray)
        {
            return $"{FormatType(type.GetElementType()!)}[]";
        }

        if (!type.IsGenericType)
        {
            return type.FullName ?? type.Name;
        }

        string name = type.GetGenericTypeDefinition().FullName
            ?? type.GetGenericTypeDefinition().Name;
        int arityMarker = name.IndexOf('`', StringComparison.Ordinal);
        if (arityMarker >= 0)
        {
            name = name[..arityMarker];
        }

        return $"{name}<{string.Join(", ", type.GetGenericArguments().Select(FormatType))}>";
    }

    private static void EnsureApplicationAssembly(Assembly assembly)
    {
        if (assembly.IsDynamic)
        {
            return;
        }

        string assemblyName = assembly.GetName().Name ?? string.Empty;
        string location = assembly.Location;
        bool isFrameworkName = assemblyName.Equals("mscorlib", StringComparison.OrdinalIgnoreCase)
            || assemblyName.Equals("netstandard", StringComparison.OrdinalIgnoreCase)
            || assemblyName.Equals("System", StringComparison.OrdinalIgnoreCase)
            || assemblyName.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
            || assemblyName.Equals("Microsoft", StringComparison.OrdinalIgnoreCase)
            || assemblyName.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase);
        bool isSharedFrameworkAssembly = !string.IsNullOrWhiteSpace(location)
            && SharedFrameworkPath is not null
            && Path.GetFullPath(location).StartsWith(
                SharedFrameworkPath,
                StringComparison.OrdinalIgnoreCase);
        if (isFrameworkName || isSharedFrameworkAssembly)
        {
            throw new InvalidOperationException(
                $"System assemblies cannot be scanned: {assemblyName}");
        }
    }

    private static string? GetSharedFrameworkPath()
    {
        var versionDirectory = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory());
        DirectoryInfo? productDirectory = versionDirectory.Parent;
        DirectoryInfo? sharedDirectory = productDirectory?.Parent;
        if (sharedDirectory is null
            || !sharedDirectory.Name.Equals("shared", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Path.GetFullPath(sharedDirectory.FullName + Path.DirectorySeparatorChar);
    }
}
