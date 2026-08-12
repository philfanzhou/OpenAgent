using System.Reflection;
using Xunit;

namespace OpenAgent.Architecture.Tests;

public class AssemblyDependencyTests
{
    [Theory]
    [InlineData("OpenAgent.Contracts")]
    [InlineData("OpenAgent.Hosting")]
    public void FoundationAssemblies_DoNotReferenceOtherOpenAgentAssemblies(string assemblyName)
    {
        AssertOpenAgentReferences(assemblyName, []);
    }

    [Fact]
    public void Core_ReferencesOnlyContracts()
    {
        AssertOpenAgentReferences("OpenAgent.Core", ["OpenAgent.Contracts"]);
    }

    [Fact]
    public void Engine_ReferencesOnlyCoreAndContracts()
    {
        AssertOpenAgentReferences(
            "OpenAgent.Engine",
            ["OpenAgent.Contracts", "OpenAgent.Core"]);
    }

    [Fact]
    public void EngineHost_ReferencesOnlyApprovedLowerLayers()
    {
        AssertOpenAgentReferences(
            "OpenAgent.Engine.Host",
            ["OpenAgent.Contracts", "OpenAgent.Core", "OpenAgent.Engine", "OpenAgent.Hosting", "OpenAgent.Infrastructure"]);
    }

    [Fact]
    public void Router_DoesNotReferenceCoreOrEngine()
    {
        AssertOpenAgentReferences(
            "OpenAgent.Router",
            ["OpenAgent.Contracts", "OpenAgent.Hosting"]);
    }

    private static void AssertOpenAgentReferences(
        string assemblyName,
        IReadOnlyCollection<string> expectedReferences)
    {
        string[] actualReferences = Assembly.Load(assemblyName)
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .Where(name => name?.StartsWith("OpenAgent.", StringComparison.Ordinal) == true)
            .Cast<string>()
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] expected = expectedReferences
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, actualReferences);
    }
}
