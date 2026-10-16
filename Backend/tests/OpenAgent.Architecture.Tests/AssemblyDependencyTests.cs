using System.Reflection;
using Xunit;

namespace OpenAgent.Architecture.Tests;

public class AssemblyDependencyTests
{
    [Theory]
    [InlineData("OpenAgent.Contracts")]
    [InlineData("OpenAgent.Router.Contracts")]
    [InlineData("OpenAgent.Hosting")]
    public void FoundationAssemblies_DoNotReferenceOtherOpenAgentAssemblies(string assemblyName)
    {
        AssertOpenAgentReferences(
            assemblyName,
            assemblyName == "OpenAgent.Hosting" ? ["OpenAgent.Contracts"] : []);
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
            ["OpenAgent.Contracts", "OpenAgent.Core", "OpenAgent.Router.Contracts"]);
    }

    [Fact]
    public void EngineHost_ReferencesOnlyApprovedLowerLayers()
    {
        AssertOpenAgentReferences(
            "OpenAgent.Engine.Host",
            ["OpenAgent.Contracts", "OpenAgent.Core", "OpenAgent.Engine", "OpenAgent.Hosting", "OpenAgent.Infrastructure"]);
    }

    [Fact]
    public void RouterCore_ReferencesOnlyContracts()
    {
        AssertOpenAgentReferences(
            "OpenAgent.Router.Core",
            ["OpenAgent.Contracts"]);
    }

    [Fact]
    public void RouterCore_DoesNotUseHttpServerOrRedisTypes()
    {
        string[] forbidden = Assembly.Load("OpenAgent.Router.Core")
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Where(name => name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)
                || name.StartsWith("Yarp", StringComparison.Ordinal)
                || name.StartsWith("StackExchange.Redis", StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(forbidden);
    }

    [Fact]
    public void RouterProviders_ReferenceOnlyRouterCoreAndContracts()
    {
        AssertOpenAgentReferences(
            "OpenAgent.Router.Providers",
            ["OpenAgent.Contracts", "OpenAgent.Router.Contracts", "OpenAgent.Router.Core"]);
    }

    [Fact]
    public void RouterHost_DoesNotReferenceCoreOrEngine()
    {
        AssertOpenAgentReferences(
            "OpenAgent.Router.Host",
            [
                "OpenAgent.Contracts",
                "OpenAgent.Hosting",
                "OpenAgent.Router.Core",
                "OpenAgent.Router.Providers"
            ]);
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
