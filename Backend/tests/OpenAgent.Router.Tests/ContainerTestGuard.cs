using Xunit;

namespace OpenAgent.Router.Tests;

internal static class ContainerTestGuard
{
    private const string EnvironmentVariable = "OPENAGENT_RUN_CONTAINER_TESTS";

    internal static bool Enabled => string.Equals(
        Environment.GetEnvironmentVariable(EnvironmentVariable),
        "1",
        StringComparison.OrdinalIgnoreCase)
        || string.Equals(
            Environment.GetEnvironmentVariable(EnvironmentVariable),
            "true",
            StringComparison.OrdinalIgnoreCase);

    internal static void RequireEnabled() => Skip.IfNot(
        Enabled,
        $"Container integration tests are disabled by default. Set {EnvironmentVariable}=1 to run them.");
}
