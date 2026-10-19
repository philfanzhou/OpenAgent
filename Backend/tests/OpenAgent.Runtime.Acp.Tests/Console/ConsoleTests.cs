using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace OpenAgent.Runtime.Acp.Tests.Console;

public sealed class ConsoleTests
{
    [Theory]
    [InlineData("list", null, 0)]
    [InlineData("run", "all", 0)]
    [InlineData("run", "missing", 2)]
    [InlineData("missing", null, 2)]
    public async Task Console_RealProcess_AllOfflineScenariosAndExitCodes(string command, string? scenario, int expected)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "OpenAgent.Runtime.Acp.TestConsole.dll");
        ProcessStartInfo start = new("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(path);
        start.ArgumentList.Add(command);
        if (scenario is not null)
        {
            start.ArgumentList.Add(scenario);
        }

        if (scenario == "all")
        {
            start.ArgumentList.Add("--json");
        }

        using Process process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(60));
            await process.WaitForExitAsync(timeout.Token);
            string text = await stdout, error = await stderr;
            Assert.True(process.ExitCode == expected, $"exit={process.ExitCode}\n{text}\n{error}");
            if (scenario == "all")
            {
                using JsonDocument report = JsonDocument.Parse(text);
                Assert.Equal(16, report.RootElement.GetArrayLength());
                foreach (JsonElement row in report.RootElement.EnumerateArray())
                {
                    Assert.Equal("PASS", row.GetProperty("status").GetString());
                }
            }
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            }
        }
    }
}
