using OpenAgent.Runtime.Acp.Processes;
using Xunit;

namespace OpenAgent.Runtime.Acp.Tests.Processes;

/// <summary>Option validation happens before anything starts, so no child process is involved.</summary>
public sealed class AcpProcessLauncherOptionsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task StartAsync_RejectsBlankExecutable(string? executable)
    {
        // Null lands as ArgumentNullException, empty/whitespace as ArgumentException.
        await Assert.ThrowsAnyAsync<ArgumentException>(() => AcpProcessLauncher.StartAsync(new AcpProcessOptions { Executable = executable! }));
    }

    [Fact]
    public async Task StartAsync_RejectsNullArgumentEntry()
    {
        AcpProcessOptions options = new() { Executable = "dotnet", Arguments = [null!] };
        await Assert.ThrowsAsync<ArgumentException>(() => AcpProcessLauncher.StartAsync(options));
    }

    [Fact]
    public async Task StartAsync_RejectsNegativeGrace()
    {
        AcpProcessOptions options = new() { Executable = "dotnet", ShutdownGracePeriod = TimeSpan.FromMilliseconds(-1) };
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => AcpProcessLauncher.StartAsync(options));
    }

    [Fact]
    public async Task StartAsync_RejectsEmptyEnvironmentKey()
    {
        AcpProcessOptions options = new() { Executable = "dotnet", Environment = new Dictionary<string, string> { [""] = "value" } };
        await Assert.ThrowsAsync<ArgumentException>(() => AcpProcessLauncher.StartAsync(options));
    }

    [Fact]
    public async Task StartAsync_RejectsNullEnvironmentValue()
    {
        AcpProcessOptions options = new() { Executable = "dotnet", Environment = new Dictionary<string, string> { ["KEY"] = null! } };
        await Assert.ThrowsAsync<ArgumentException>(() => AcpProcessLauncher.StartAsync(options));
    }

    [Fact]
    public async Task StartAsync_RejectsMissingWorkingDirectoryBeforeStartingAnything()
    {
        string missing = Path.Combine(Path.GetTempPath(), "acp-tests-missing-cwd-" + Guid.NewGuid().ToString("N"));
        AcpProcessOptions options = new() { Executable = "dotnet", WorkingDirectory = missing };
        await Assert.ThrowsAsync<ArgumentException>(() => AcpProcessLauncher.StartAsync(options));
    }
}
