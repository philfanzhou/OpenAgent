using System.Reflection;
using Xunit;

namespace OpenAgent.Architecture.Tests;

public class ApplicationModuleDependencyTests
{
    // This is the enforceable source of truth for Application and Capabilities module dependencies.
    private static readonly IReadOnlyDictionary<string, string[]> Allowed = new Dictionary<string, string[]>
    {
        ["Mapping"] = [],
        ["Observability"] = ["Mapping"],
        ["ModelProviders"] = ["Mapping", "Observability"],
        ["Runner"] = [],
        ["Security"] = [],
        ["Tooling"] = ["Mapping", "Observability", "Security"],
        ["Files"] = ["Mapping"],
        ["FileTools"] = ["Files", "Tooling", "Runner", "Security", "Mapping"],
        ["Conversation"] = ["Files", "Mapping", "ModelProviders", "Observability"],
        ["Mcp"] = ["Files", "Tooling", "Security", "Mapping"],
        ["Skill"] = ["Files", "Runner", "Tooling", "Security", "Mapping"],
        ["Rag"] = ["Tooling", "Security", "Mapping"],
        ["Code"] = ["Files", "Runner", "Tooling", "Security", "Mapping"],
        ["Workspace"] = ["Files", "Runner", "Tooling", "Security", "Mapping"],
        ["Plan"] = ["Tooling"],
        ["UserProfile"] = ["Tooling"],
        ["Execution"] = ["Files", "Conversation", "ModelProviders", "Tooling", "Security", "Mapping", "Observability"]
    };

    [Fact]
    public void Modules_ReferenceOnlyApprovedModules()
    {
        Assembly[] assemblies = [Assembly.Load("OpenAgent.Application"), Assembly.Load("OpenAgent.Capabilities")];
        List<string> violations = [];
        foreach (Type source in assemblies.SelectMany(assembly => assembly.GetTypes()))
        {
            string module = ModuleOf(source);
            if (module is "Composition" or "Compiler")
            {
                continue;
            }
            Assert.True(Allowed.ContainsKey(module), $"Unowned module type: {source.FullName}");
            foreach (Type target in TypeDependencies.Read(source).Where(type => assemblies.Contains(type.Assembly)))
            {
                string targetModule = ModuleOf(target);
                if (targetModule != "Compiler" && targetModule != module && !Allowed[module].Contains(targetModule, StringComparer.Ordinal))
                {
                    violations.Add($"{source.FullName} ({module}) -> {target.FullName} ({targetModule})");
                }
            }
        }
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations.Distinct().Order()));
    }

    [Fact]
    public void DependencyReader_IncludesStaticCallsAndGenericSignatures()
    {
        IReadOnlySet<Type> dependencies = TypeDependencies.Read(typeof(Caller));
        Assert.Contains(typeof(StaticTarget), dependencies);
        Assert.Contains(typeof(SignatureTarget), dependencies);
    }

    private static string ModuleOf(Type type)
    {
        while (type.HasElementType)
        {
            type = type.GetElementType()!;
        }
        while (type.DeclaringType != null)
        {
            type = type.DeclaringType;
        }
        // Public identities retained for compatibility after their files move.
        if (type.FullName == "OpenAgent.Application.Runtime.Agent.AgentExecutionOptions") return "Tooling";
        if (type.FullName == "OpenAgent.Application.Runtime.ExecutionFailureDescriptor") return "Mapping";
        if (type.Namespace == "OpenAgent.Contracts.Capabilities")
        {
            return type.Name switch
            {
                "ISkillCatalog" => "Skill",
                "IRagRegistry" or "IRagService" => "Rag",
                _ => "Unknown"
            };
        }
        if (type.Namespace == null && type.Name.StartsWith("<", StringComparison.Ordinal)) return "Compiler";
        string[] parts = (type.Namespace ?? "").Split('.');
        if (parts.Length < 3) return "Unknown";
        return parts[2] switch
        {
            "Exten" or "Extensions" => "Composition",
            "Integrations" when parts.ElementAtOrDefault(3) == "Runner" => "Runner",
            "Capabilities" when parts.ElementAtOrDefault(1) == "Application" => parts.ElementAtOrDefault(3) ?? "Unknown",
            "Skills" => "Skill",
            "Files" when parts.ElementAtOrDefault(3) == "Tools" => "FileTools",
            _ => parts[2]
        };
    }

    private sealed class Caller
    {
        public List<SignatureTarget> Call()
        {
            StaticTarget.Invoke();
            return [];
        }
    }

    private sealed class SignatureTarget;
    private static class StaticTarget
    {
        internal static void Invoke() { }
    }
}
