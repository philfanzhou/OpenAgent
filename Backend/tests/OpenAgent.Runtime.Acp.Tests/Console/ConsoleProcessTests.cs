using System.Diagnostics;
using Xunit;

namespace OpenAgent.Runtime.Acp.Tests.Console;

public sealed class ConsoleProcessTests
{
    [Theory]
    [InlineData("list", null, 0, "framing")]
    [InlineData("run", "framing", 0, "PASS framing")]
    [InlineData("run", "lifecycle", 0, "PASS lifecycle")]
    [InlineData("run", "all", 0, "PASS lifecycle")]
    [InlineData("run", "missing", 2, "Unknown command")]
    public async Task Main_ScenarioCommand_ReturnsExpectedResult(string command, string? scenario, int exitCode, string text)
    {
        string consolePath = Path.Combine(AppContext.BaseDirectory, "OpenAgent.Runtime.Acp.TestConsole.dll");
        string testRuntime = Directory.GetFiles(AppContext.BaseDirectory, "OpenAgent.Runtime.Acp.Tests.runtimeconfig.json").Single();
        string consoleDeps = Path.Combine(AppContext.BaseDirectory, "OpenAgent.Runtime.Acp.TestConsole.deps.json");
        ProcessStartInfo start = new("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add("--runtimeconfig");
        start.ArgumentList.Add(testRuntime);
        if (File.Exists(consoleDeps))
        {
            start.ArgumentList.Add("--depsfile");
            start.ArgumentList.Add(consoleDeps);
        }

        start.ArgumentList.Add(consolePath);
        start.ArgumentList.Add(command);
        if (scenario is not null)
        {
            start.ArgumentList.Add(scenario);
        }

        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start console.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(exitCode, process.ExitCode);
            Assert.Contains(text, await stdout + await stderr);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }
}
