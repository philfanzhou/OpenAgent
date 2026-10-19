using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace OpenAgent.Runtime.Acp.Tests.Console;

public sealed class ConsoleProcessTests
{
    [Theory]
    [InlineData("list", null, 0, "echo\tP3")]
    [InlineData("run", "framing", 0, "PASS framing")]
    [InlineData("run", "lifecycle", 0, "PASS lifecycle")]
    [InlineData("run", "schema", 0, "methods=25 updates=14 definitions=178")]
    [InlineData("run", "rpc", 0, "PASS rpc")]
    [InlineData("run", "reverse", 0, "PASS reverse")]
    [InlineData("run", "fault", 0, "PASS fault")]
    [InlineData("run", "echo", 0, "PASS echo (client/agent minimal loop)")]
    [InlineData("run", "cancel", 0, "PASS cancel (client/agent minimal loop)")]
    [InlineData("run", "process", 0, "PASS process (SDK process lifecycle)")]
    [InlineData("run", "all", 0, "PASS cancel")]
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
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Main_SchemaReport_RecordsSuccessAndRealProcessFailure(bool badFixture)
    {
        string directory = Path.Combine(Path.GetTempPath(), "acp-schema-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string report = Path.Combine(directory, "report.json");
        string fixture = Path.Combine(directory, "invalid.json");
        await File.WriteAllTextAsync(fixture, "{\"protocolVersion\":2,\"sourceCommit\":\"invalid\",\"cases\":[]}");
        ProcessStartInfo start = new("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string arg in new[] { "exec", "--runtimeconfig", Path.Combine(AppContext.BaseDirectory, "OpenAgent.Runtime.Acp.Tests.runtimeconfig.json"), Path.Combine(AppContext.BaseDirectory, "OpenAgent.Runtime.Acp.TestConsole.dll"), "run", "schema", "--report", report }) start.ArgumentList.Add(arg);
        if (badFixture) { start.ArgumentList.Add("--fixtures"); start.ArgumentList.Add(fixture); }
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start console.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(badFixture ? 1 : 0, process.ExitCode);
            using JsonDocument json = JsonDocument.Parse(await File.ReadAllTextAsync(report));
            Assert.Equal(badFixture ? "FAIL" : "PASS", json.RootElement.GetProperty("status").GetString());
            JsonElement result = json.RootElement.GetProperty("results")[0];
            Assert.Equal("P1", result.GetProperty("phase").GetString());
            Assert.Equal(1, result.GetProperty("protocolVersion").GetInt32());
            Assert.True(result.GetProperty("elapsedMs").GetDouble() >= 0);
            if (badFixture)
            {
                Assert.Equal("InvalidDataException", result.GetProperty("error").GetString());
                Assert.Contains("FAIL schema", await stderr);
                Assert.DoesNotContain(fixture, await stderr);
            }
            else
            {
                Assert.Equal(25, result.GetProperty("coverage").GetProperty("methodCount").GetInt32());
                Assert.Equal(14, result.GetProperty("coverage").GetProperty("updateCount").GetInt32());
                Assert.Contains("PASS schema", await stdout);
            }
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            Directory.Delete(directory, recursive: true);
        }
    }

}
