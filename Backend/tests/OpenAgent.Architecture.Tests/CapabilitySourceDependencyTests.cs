using System.Reflection;
using OpenAgent.Contracts.Configuration;
using Xunit;

namespace OpenAgent.Architecture.Tests;

/// <summary>
/// PR-9 守门规则：能力发现链（OpenAgent.Core 的 Capabilities/Files 下
/// ICapabilitySource 实现方）不得引用 AgentConfig 根类型，只能消费
/// CapabilityContext 或具体小节类型，防止"整只传入"的依赖回潮。
/// </summary>
public class CapabilitySourceDependencyTests
{
    private const string InterfaceFullName = "OpenAgent.Core.Capabilities.ICapabilitySource";

    [Fact]
    public void CapabilitySources_DoNotReferenceAgentConfigRootType()
    {
        Assembly core = Assembly.Load("OpenAgent.Core");
        Type? interfaceType = core.GetType(InterfaceFullName);
        Assert.NotNull(interfaceType);

        Type[] implementations = LoadCoreTypes(core)
            .Where(type => type is { IsInterface: false, IsAbstract: false }
                && interfaceType.IsAssignableFrom(type)
                && BelongsToCapabilityNamespaces(type))
            .ToArray();

        // 防止规则悄悄失效：仓库内置的四个能力源必须都被扫到。
        Assert.Contains(implementations, type => type.Name == "CodeCapabilitySource");
        Assert.Contains(implementations, type => type.Name == "RagCapabilitySource");
        Assert.Contains(implementations, type => type.Name == "UserProfileCapabilitySource");
        Assert.Contains(implementations, type => type.Name == "FileAssetCapabilitySource");

        string[] offenders = implementations
            .Append(interfaceType)
            .SelectMany(OffendingMembers)
            .ToArray();

        Assert.Empty(offenders);
    }

    private static IEnumerable<Type> LoadCoreTypes(Assembly core)
    {
        try
        {
            return core.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.Where(type => type != null).Cast<Type>();
        }
    }

    private static bool BelongsToCapabilityNamespaces(Type type) =>
        type.Namespace?.StartsWith("OpenAgent.Core.Capabilities", StringComparison.Ordinal) == true
        || type.Namespace?.StartsWith("OpenAgent.Core.Files", StringComparison.Ordinal) == true;

    private static IEnumerable<string> OffendingMembers(Type type)
    {
        const BindingFlags AllMembers =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        foreach (ConstructorInfo constructor in type.GetConstructors(AllMembers))
        {
            foreach (string offender in OffendingParameters($"{type.Name}..ctor", constructor.GetParameters()))
            {
                yield return offender;
            }
        }

        foreach (MethodInfo method in type.GetMethods(AllMembers | BindingFlags.DeclaredOnly))
        {
            foreach (string offender in OffendingParameters($"{type.Name}.{method.Name}", method.GetParameters()))
            {
                yield return offender;
            }
            if (ReferencesAgentConfig(method.ReturnType))
            {
                yield return $"{type.Name}.{method.Name} -> {method.ReturnType.Name}";
            }
        }

        foreach (PropertyInfo property in type.GetProperties(AllMembers | BindingFlags.DeclaredOnly)
            .Where(property => ReferencesAgentConfig(property.PropertyType)))
        {
            yield return $"{type.Name}.{property.Name} : {property.PropertyType.Name}";
        }

        foreach (FieldInfo field in type.GetFields(AllMembers | BindingFlags.DeclaredOnly)
            .Where(field => ReferencesAgentConfig(field.FieldType)))
        {
            yield return $"{type.Name}.{field.Name} : {field.FieldType.Name}";
        }
    }

    private static IEnumerable<string> OffendingParameters(string memberName, ParameterInfo[] parameters) =>
        parameters
            .Where(parameter => ReferencesAgentConfig(parameter.ParameterType))
            .Select(parameter => $"{memberName}({parameter.Name} : {parameter.ParameterType.Name})");

    private static bool ReferencesAgentConfig(Type type)
    {
        if (type == typeof(AgentConfig))
        {
            return true;
        }
        if (type.HasElementType)
        {
            return ReferencesAgentConfig(type.GetElementType()!);
        }
        return type.IsGenericType
            && type.GetGenericArguments().Any(ReferencesAgentConfig);
    }
}
