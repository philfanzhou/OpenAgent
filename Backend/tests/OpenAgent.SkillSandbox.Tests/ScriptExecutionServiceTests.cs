using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Contracts.Skills;
using OpenAgent.SkillSandbox.Host;
using Xunit;

namespace OpenAgent.SkillSandbox.Tests;

public sealed class ScriptExecutionServiceTests
{
    [Fact]
    public async Task ExecuteAsync_PythonScript_ReturnsOutputAndArguments()
    {
        ScriptExecutionService service = CreateService();

        SkillScriptExecutionResult result = await service.ExecuteAsync(
            Request("import sys\nprint('|'.join(sys.argv[1:]))", "alpha", "beta"),
            default);

        Assert.True(result.Success);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("alpha|beta", result.StandardOutput.Trim());
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task ExecuteAsync_Timeout_KillsScript()
    {
        ScriptExecutionService service = CreateService(timeoutSeconds: 1);

        SkillScriptExecutionResult result = await service.ExecuteAsync(
            Request("import time\ntime.sleep(10)"),
            default);

        Assert.False(result.Success);
        Assert.True(result.TimedOut);
    }

    [Fact]
    public async Task ExecuteAsync_OutputLimit_KillsScriptAndMarksTruncated()
    {
        ScriptExecutionService service = CreateService(maxOutputBytes: 32);

        SkillScriptExecutionResult result = await service.ExecuteAsync(
            Request("print('x' * 1024)"),
            default);

        Assert.False(result.Success);
        Assert.True(result.OutputTruncated);
        Assert.True(Encoding.UTF8.GetByteCount(result.StandardOutput) <= 32);
    }

    [Fact]
    public async Task ExecuteAsync_OutputLimit_AppliesAcrossStdoutAndStderr()
    {
        ScriptExecutionService service = CreateService(maxOutputBytes: 32);

        SkillScriptExecutionResult result = await service.ExecuteAsync(
            Request("import sys\nsys.stderr.write('e' * 24)\nsys.stderr.flush()\nprint('o' * 24)"),
            default);

        int capturedBytes = Encoding.UTF8.GetByteCount(result.StandardOutput)
            + Encoding.UTF8.GetByteCount(result.StandardError);
        Assert.False(result.Success);
        Assert.True(result.OutputTruncated);
        Assert.True(capturedBytes <= 32);
    }

    [Fact]
    public async Task ExecuteAsync_BackgroundChild_DoesNotKeepExecutionOpen()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/setsid"))
        {
            return;
        }

        ScriptExecutionService service = CreateService();
        const string script = """
            import subprocess
            import sys
            subprocess.Popen([sys.executable, '-c', 'import time; time.sleep(30)'])
            print('parent-finished')
            """;

        SkillScriptExecutionResult result = await service.ExecuteAsync(
            Request(script),
            default).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.Success);
        Assert.Contains("parent-finished", result.StandardOutput, StringComparison.Ordinal);
    }

    private static ScriptExecutionService CreateService(
        int timeoutSeconds = 5,
        int maxOutputBytes = 4096) =>
        new(
            new SandboxOptions
            {
                Interpreter = "/usr/bin/python3",
                TimeoutSeconds = timeoutSeconds,
                MaxOutputBytes = maxOutputBytes
            },
            NullLogger<ScriptExecutionService>.Instance);

    private static SkillScriptExecutionRequest Request(
        string script,
        params string[] arguments) =>
        new()
        {
            SkillName = "python-test",
            ScriptName = "test.py",
            Script = Encoding.UTF8.GetBytes(script),
            Arguments = arguments
        };
}
