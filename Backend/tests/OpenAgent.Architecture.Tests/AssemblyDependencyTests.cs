using System.Reflection;
using Xunit;

namespace OpenAgent.Architecture.Tests;

public class AssemblyDependencyTests
{
    [Theory]
    [InlineData("OpenAgent.Contracts", new string[] { })]
    [InlineData("OpenAgent.Capabilities", new[] { "OpenAgent.Contracts" })]
    [InlineData("OpenAgent.Engine", new[] { "OpenAgent.Contracts" })]
    [InlineData("OpenAgent.Framework", new[] { "OpenAgent.Contracts" })]
    [InlineData("OpenAgent.Runner", new[] { "OpenAgent.Contracts" })]
    [InlineData("OpenAgent.Application", new[] { "OpenAgent.Capabilities", "OpenAgent.Contracts" })]
    [InlineData("OpenAgent.Router", new[] { "OpenAgent.Contracts", "OpenAgent.Engine" })]
    [InlineData("OpenAgent.Engine.Host", new[] { "OpenAgent.Application", "OpenAgent.Contracts", "OpenAgent.Engine", "OpenAgent.Framework" })]
    public void Assemblies_ReferenceOnlyApprovedLayers(string assemblyName, string[] expected)
    {
        string[] actual = Assembly.Load(assemblyName)
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .Where(name => name?.StartsWith("OpenAgent.", StringComparison.Ordinal) == true)
            .Cast<string>()
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected.Order(StringComparer.Ordinal), actual);
    }

    [Fact]
    public void Capabilities_DoNotReferenceAgentOrProtocolSdks()
    {
        string[] references = Assembly.Load("OpenAgent.Capabilities")
            .GetReferencedAssemblies()
            .Select(reference => reference.Name!)
            .ToArray();

        Assert.DoesNotContain(references, name =>
            name.StartsWith("Microsoft.Agents.", StringComparison.Ordinal)
            || name.StartsWith("Microsoft.Extensions.AI", StringComparison.Ordinal)
            || name.StartsWith("ModelContextProtocol", StringComparison.Ordinal)
            || name.StartsWith("OpenAI", StringComparison.Ordinal));
    }

    [Fact]
    public void Router_UsesOnlyEngineHostingTypes()
    {
        Assembly router = Assembly.Load("OpenAgent.Router");
        Assembly engine = Assembly.Load("OpenAgent.Engine");
        string[] violations = router.GetTypes()
            .SelectMany(TypeDependencies.Read)
            .Where(type => type.Assembly == engine)
            .Where(type => type.Namespace?.StartsWith("OpenAgent.Engine.Hosting", StringComparison.Ordinal) != true)
            .Select(type => type.FullName!)
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(violations);
    }
}
