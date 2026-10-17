using System.Diagnostics;
using System.Reflection;
using Xunit;

namespace OpenAgent.Runtime.Acp.Tests.Protocol;

public sealed class CodeGenProcessTests
{
    [Fact]
    public async Task GeneratedSources_TwoIndependentProcesses_MatchCheckedInBytes()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Backend/OpenAgent.sln"))) root = root.Parent;
        Assert.NotNull(root);
        string configuration = typeof(CodeGenProcessTests).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
        string generator = Path.Combine(root.FullName, "Backend/tools/OpenAgent.Runtime.Acp.CodeGen/bin", configuration, "net10.0/OpenAgent.Runtime.Acp.CodeGen.dll");
        Assert.True(File.Exists(generator), "Build the test project or main solution, including CodeGen, before testing.");
        for (int run = 0; run < 2; run++)
        {
            ProcessStartInfo start = new("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (string argument in new[] { generator, root.FullName, "--check" }) start.ArgumentList.Add(argument);
            using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start CodeGen.");
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            try
            {
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
                await process.WaitForExitAsync(timeout.Token);
                Assert.True(process.ExitCode == 0, await stderr);
                Assert.Contains("Verified", await stdout);
            }
            finally
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            }
        }
    }
}
