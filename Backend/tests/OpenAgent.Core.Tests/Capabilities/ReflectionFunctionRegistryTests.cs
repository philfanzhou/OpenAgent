using System.Reflection;
using System.Reflection.Emit;
using Microsoft.Extensions.DependencyInjection;
using OpenAgent.Contracts.Capabilities;
using OpenAgent.Core.Exten;
using Xunit;

namespace OpenAgent.Core.Tests.Capabilities;

public class ReflectionFunctionRegistryTests
{
    [Fact]
    public void Functions_ConfiguredAssembly_ScansOnlyConfiguredScope()
    {
        Assembly configured = CreateAssembly(
            new MethodSpec("ConfiguredFunctions", "Echo", typeof(string), [typeof(int)]));
        Assembly unconfigured = CreateAssembly(
            new MethodSpec("UnconfiguredFunctions", "Hidden", typeof(void), []));
        using ServiceProvider provider = CreateProvider(configured);

        IReadOnlyList<ReflectionFunctionDescriptor> functions = provider
            .GetRequiredService<IReflectionFunctionRegistry>()
            .Functions;

        ReflectionFunctionDescriptor function = Assert.Single(functions);
        Assert.Equal("Echo", function.Name);
        Assert.Equal(configured.GetName().Name, function.AssemblyName);
        Assert.DoesNotContain(functions, item => item.AssemblyName == unconfigured.GetName().Name);
        Assert.Equal(
            "System.String ConfiguredFunctions.Echo(System.Int32 value0)",
            function.Signature);
    }

    [Fact]
    public void Functions_DuplicateNames_Throws()
    {
        Assembly assembly = CreateAssembly(
            new MethodSpec("FirstFunctions", "Duplicate", typeof(void), []),
            new MethodSpec("SecondFunctions", "Duplicate", typeof(void), []));
        using ServiceProvider provider = CreateProvider(assembly);
        IReflectionFunctionRegistry registry = provider
            .GetRequiredService<IReflectionFunctionRegistry>();

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => registry.Functions);

        Assert.Contains("Duplicate reflection function name: Duplicate", exception.Message);
    }

    [Fact]
    public void Functions_InvalidSignature_ExcludesMethod()
    {
        Assembly assembly = CreateAssembly(
            new MethodSpec("ValidFunctions", "Valid", typeof(void), []),
            new MethodSpec(
                "InvalidFunctions",
                "Invalid",
                typeof(void),
                [typeof(int).MakeByRefType()]));
        using ServiceProvider provider = CreateProvider(assembly);

        IReadOnlyList<ReflectionFunctionDescriptor> functions = provider
            .GetRequiredService<IReflectionFunctionRegistry>()
            .Functions;

        ReflectionFunctionDescriptor function = Assert.Single(functions);
        Assert.Equal("Valid", function.Name);
    }

    [Fact]
    public void Functions_TrustedPlatformAssembly_Throws()
    {
        using ServiceProvider provider = CreateProvider(typeof(string).Assembly);
        IReflectionFunctionRegistry registry = provider
            .GetRequiredService<IReflectionFunctionRegistry>();

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => registry.Functions);

        Assert.Contains("System assemblies cannot be scanned", exception.Message);
    }

    [Fact]
    public void Functions_NotEnabled_ReturnsEmpty()
    {
        Assembly assembly = CreateAssembly(
            new MethodSpec("DisabledFunctions", "Disabled", typeof(void), []));
        var services = new ServiceCollection();
        services.AddReflectionFunctions(options => options.Assemblies.Add(assembly));
        using ServiceProvider provider = services.BuildServiceProvider();

        IReadOnlyList<ReflectionFunctionDescriptor> functions = provider
            .GetRequiredService<IReflectionFunctionRegistry>()
            .Functions;

        Assert.Empty(functions);
    }

    private static ServiceProvider CreateProvider(params Assembly[] assemblies)
    {
        var services = new ServiceCollection();
        services.AddReflectionFunctions(options =>
        {
            options.Enabled = true;
            foreach (Assembly assembly in assemblies)
            {
                options.Assemblies.Add(assembly);
            }
        });
        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private static Assembly CreateAssembly(params MethodSpec[] methods)
    {
        var assemblyName = new AssemblyName($"ReflectionFunctions_{Guid.NewGuid():N}");
        AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly(
            assemblyName,
            AssemblyBuilderAccess.Run);
        ModuleBuilder module = assembly.DefineDynamicModule(assemblyName.Name!);

        foreach (IGrouping<string, MethodSpec> typeMethods in methods.GroupBy(method => method.TypeName))
        {
            TypeBuilder type = module.DefineType(
                typeMethods.Key,
                TypeAttributes.Public
                | TypeAttributes.Abstract
                | TypeAttributes.Sealed);
            foreach (MethodSpec method in typeMethods)
            {
                MethodBuilder builder = type.DefineMethod(
                    method.Name,
                    MethodAttributes.Public | MethodAttributes.Static,
                    method.ReturnType,
                    method.ParameterTypes);
                for (int index = 0; index < method.ParameterTypes.Length; index++)
                {
                    builder.DefineParameter(index + 1, ParameterAttributes.None, $"value{index}");
                }

                EmitDefaultReturn(builder.GetILGenerator(), method.ReturnType);
            }

            type.CreateType();
        }

        return assembly;
    }

    private static void EmitDefaultReturn(ILGenerator generator, Type returnType)
    {
        if (returnType == typeof(string))
        {
            generator.Emit(OpCodes.Ldstr, string.Empty);
        }
        else if (returnType != typeof(void))
        {
            LocalBuilder result = generator.DeclareLocal(returnType);
            generator.Emit(OpCodes.Ldloca_S, result);
            generator.Emit(OpCodes.Initobj, returnType);
            generator.Emit(OpCodes.Ldloc, result);
        }

        generator.Emit(OpCodes.Ret);
    }

    private sealed record MethodSpec(
        string TypeName,
        string Name,
        Type ReturnType,
        Type[] ParameterTypes);
}
