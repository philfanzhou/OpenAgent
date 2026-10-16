using OpenAgent.Runtime.Acp.Processes;
using Xunit;
namespace OpenAgent.Runtime.Acp.Tests.Processes;
public sealed class ProcessTests
{
    internal static AcpProcessOptions Command(string mode) => new("dotnet", [Path.Combine(AppContext.BaseDirectory, "OpenAgent.Runtime.Acp.TestConsole.dll"), mode]);
    [Fact]
    public async Task Process_DrainsLargeStderrWithoutBlockingAndPreservesExitCode()
    {
        long chars = 0;
        await using AcpProcess process = AcpProcess.Start(Command("noisy-worker") with { StandardError = value => Interlocked.Add(ref chars, value.Length) });
        Assert.Equal(17, await process.Exit.WaitAsync(TimeSpan.FromSeconds(10))); Assert.Equal(4 * 1024 * 1024, chars);
    }
    [Fact]
    public async Task Process_DisposeKillsOnlyOwnedUnresponsiveProcessAndIsIdempotent()
    {
        AcpProcess process = AcpProcess.Start(Command("stall-worker") with { ShutdownGracePeriod = TimeSpan.FromMilliseconds(50) });
        await Task.WhenAll(process.DisposeAsync().AsTask(), process.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(process.Exit.IsCompleted); Assert.NotEqual(0, await process.Exit);
    }
}
