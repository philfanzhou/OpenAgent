using OpenAgent.Core.Execution;
using Xunit;

namespace OpenAgent.Core.Tests.Execution;

public sealed class AcpLaunchTests
{
    [Fact]
    public void Create_LocalModePreservesLoginHome_OnlyCopiesNamedVariables()
    {
        string workspace = Path.Combine(Path.GetTempPath(), "acp-launch-" + Guid.NewGuid().ToString("N"));
        string allowed = "ACP_ALLOWED_" + Guid.NewGuid().ToString("N");
        string unrelated = "ACP_UNRELATED_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(allowed, "allowed-value");
        Environment.SetEnvironmentVariable(unrelated, "private-value");
        try
        {
            var native = AcpLaunch.Create(new AcpHarness { Command = "native", EnvironmentMode = "local",
                InheritedEnvironmentVariables = [allowed], Model = "native-model", ModelEnvironmentVariable = "MODEL" },
                workspace, AcpRuntimeAdapterTests.Invocation("hello"));
            native.Environment.TryGetValue("HOME", out string? home);
            Assert.Equal(Environment.GetEnvironmentVariable("HOME"), home);
            Assert.Equal("allowed-value", native.Environment[allowed]);
            Assert.False(native.Environment.ContainsKey(unrelated));
            Assert.Equal("native-model", native.Environment["MODEL"]);
            var isolated = AcpLaunch.Create(new AcpHarness { Command = "native" }, workspace, AcpRuntimeAdapterTests.Invocation("hello"));
            Assert.Equal(Path.Combine(workspace, ".harness-home"), isolated.Environment["HOME"]);
            Assert.False(isolated.Environment.ContainsKey(allowed));
        }
        finally
        {
            Environment.SetEnvironmentVariable(allowed, null);
            Environment.SetEnvironmentVariable(unrelated, null);
            if (Directory.Exists(workspace)) Directory.Delete(workspace, recursive: true);
        }
    }
}
