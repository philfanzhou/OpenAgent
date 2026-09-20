using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Execution;
using Xunit;

namespace OpenAgent.Runner.Tests;

public class SessionSandboxExecutionTests
{
    [BubblewrapFact]
    public async Task Execute_SessionSandboxPersistsFilesAcrossCalls()
    {
        await using var runtime = new SessionRuntime();
        CodeExecutionResult setup = await runtime.ExecuteAsync("conv", """
            import os
            from pathlib import Path
            assert os.getuid() == 65532
            Path('/work/state.txt').write_text('persist-me')
            Path('/tmp/cache.txt').write_text('temp')
            Path('/input/notes.txt').write_text('input-side')
            print('setup-done')
            """);
        Assert.True(setup.ExitCode == 0, setup.Stderr);
        Assert.False(setup.SandboxReset);

        CodeExecutionResult verify = await runtime.ExecuteAsync("conv", """
            import os
            from pathlib import Path
            assert os.getuid() == 65532
            assert Path('/work/state.txt').read_text() == 'persist-me'
            assert Path('/tmp/cache.txt').read_text() == 'temp'
            assert Path('/input/notes.txt').read_text() == 'input-side'
            try:
                Path('/usr/escape').write_text('x')
            except OSError:
                pass
            else:
                raise AssertionError('root filesystem is writable')
            print('persisted')
            """);
        Assert.True(verify.ExitCode == 0, verify.Stderr);
        Assert.Contains("persisted", verify.Stdout);
        Assert.False(verify.SandboxReset);
        Assert.Equal(1, runtime.Manager.LiveCount);
    }

    [BubblewrapFact]
    public async Task Execute_SessionsAreIsolated()
    {
        await using var runtime = new SessionRuntime();
        await runtime.ExecuteAsync("conv-a", "from pathlib import Path\nPath('/work/a.txt').write_text('alpha')");
        CodeExecutionResult probe = await runtime.ExecuteAsync("conv-b",
            "from pathlib import Path\nassert not Path('/work/a.txt').exists()\nPath('/work/b.txt').write_text('beta')\nprint('isolated')");
        Assert.True(probe.ExitCode == 0, probe.Stderr);
        CodeExecutionResult back = await runtime.ExecuteAsync("conv-a",
            "from pathlib import Path\nassert Path('/work/a.txt').read_text() == 'alpha'\nassert not Path('/work/b.txt').exists()\nprint('own-state')");
        Assert.True(back.ExitCode == 0, back.Stderr);
        Assert.Equal(2, runtime.Manager.LiveCount);
    }

    [BubblewrapFact]
    public async Task Execute_SessionSandboxSurvivesTimeoutAndKeepsState()
    {
        await using var runtime = new SessionRuntime(timeoutSeconds: 2);
        await runtime.ExecuteAsync("conv", "from pathlib import Path\nPath('/work/before.txt').write_text('kept')");
        CodeExecutionResult timedOut = await runtime.ExecuteAsync("conv",
            "import time\ntime.sleep(600)");
        Assert.True(timedOut.TimedOut);
        CodeExecutionResult after = await runtime.ExecuteAsync("conv",
            "from pathlib import Path\nassert Path('/work/before.txt').read_text() == 'kept'\nprint('survived')");
        Assert.True(after.ExitCode == 0, after.Stderr);
        Assert.Contains("survived", after.Stdout);
        Assert.Equal(1, runtime.Manager.LiveCount);
    }

    [BubblewrapFact]
    public async Task Execute_SandboxDeathRespawnsFreshSandboxWithReset()
    {
        await using var runtime = new SessionRuntime();
        await runtime.ExecuteAsync("conv", "from pathlib import Path\nPath('/work/state.txt').write_text('gone-soon')");
        var sandbox = Assert.IsType<SessionSandbox>(runtime.Manager.GetSandbox("conv"));
        sandbox.Process.Kill();
        sandbox.Process.WaitForExit();

        CodeExecutionResult respawned = await runtime.ExecuteAsync("conv", """
            from pathlib import Path
            assert not Path('/work/state.txt').exists()
            print('fresh')
            """);
        Assert.True(respawned.ExitCode == 0, respawned.Stderr);
        Assert.True(respawned.SandboxReset);
        Assert.Equal(1, runtime.Manager.LiveCount);
    }

    [BubblewrapFact]
    public async Task Execute_EvictsAtCapacityAndNextRunReportsReset()
    {
        await using var runtime = new SessionRuntime(maxSessionSandboxes: 1);
        await runtime.ExecuteAsync("alpha", "from pathlib import Path\nPath('/work/a.txt').write_text('a')");
        CodeExecutionResult beta = await runtime.ExecuteAsync("beta", "print('beta')");
        Assert.True(beta.ExitCode == 0, beta.Stderr);
        Assert.Equal(1, runtime.Manager.LiveCount);
        Assert.False(runtime.Manager.IsLive("alpha"));

        CodeExecutionResult alphaAgain = await runtime.ExecuteAsync("alpha",
            "from pathlib import Path\nassert not Path('/work/a.txt').exists()\nprint('alpha-back')");
        Assert.True(alphaAgain.ExitCode == 0, alphaAgain.Stderr);
        Assert.True(alphaAgain.SandboxReset);
    }

    [BubblewrapFact]
    public async Task Execute_ReapReleasesIdleSandboxAndNextRunReportsReset()
    {
        await using var runtime = new SessionRuntime();
        await runtime.ExecuteAsync("conv", "print('warm')");
        Assert.Equal(1, runtime.Manager.LiveCount);
        await Task.Delay(TimeSpan.FromSeconds(1.2));
        await runtime.Manager.ReapIdleAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(0, runtime.Manager.LiveCount);
        // The session directory stays behind as the reset marker; the channel is gone.
        Assert.True(Directory.Exists(Path.Combine(runtime.Root, "session-conv")));
        Assert.False(Directory.Exists(Path.Combine(runtime.Root, "session-conv", "channel")));

        CodeExecutionResult after = await runtime.ExecuteAsync("conv", "print('back')");
        Assert.True(after.ExitCode == 0, after.Stderr);
        Assert.True(after.SandboxReset);
    }

    [BubblewrapFact]
    public async Task Execute_SessionReturnsOnlyNewOutputFilesButKeepsOldOnesReadable()
    {
        await using var runtime = new SessionRuntime();
        CodeExecutionResult first = await runtime.ExecuteAsync("conv",
            "from pathlib import Path\nPath('/output/keep.txt').write_text('one')");
        Assert.Equal("keep.txt", Assert.Single(first.Files).Name);

        CodeExecutionResult second = await runtime.ExecuteAsync("conv", """
            from pathlib import Path
            assert Path('/output/keep.txt').read_text() == 'one'
            print('kept-readable')
            """);
        Assert.True(second.ExitCode == 0, second.Stderr);
        Assert.Empty(second.Files);
    }

    [BubblewrapFact]
    public async Task Execute_SessionSupportsLanguageSwitchWithinOneSandbox()
    {
        await using var runtime = new SessionRuntime();
        CodeExecutionResult javascript = await runtime.Executor.ExecuteAsync(new CodeExecutionRequest
        {
            SessionKey = "conv",
            Language = ExecutionLanguage.JavaScript,
            Code = "import { writeFile } from 'node:fs/promises';\nawait writeFile('/work/data.txt', 'from-node');\nconsole.log('node-done')"
        }, CancellationToken.None);
        Assert.True(javascript.ExitCode == 0, javascript.Stderr);

        CodeExecutionResult python = await runtime.ExecuteAsync("conv",
            "from pathlib import Path\nassert Path('/work/data.txt').read_text() == 'from-node'\nprint('python-reads-node')");
        Assert.True(python.ExitCode == 0, python.Stderr);
        Assert.Contains("python-reads-node", python.Stdout);
    }

    private sealed class SessionRuntime : IAsyncDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "codeact-session-tests-" + Guid.NewGuid().ToString("N"));
        internal SessionSandboxManager Manager { get; }
        internal BubblewrapCodeExecutor Executor { get; }

        internal SessionRuntime(int timeoutSeconds = 120, int maxSessionSandboxes = 64, int sessionIdleMinutes = 120)
        {
            Directory.CreateDirectory(Root);
            var settings = Options.Create(new RunnerOptions
            {
                WorkspaceRoot = Root,
                BubblewrapPath = Environment.GetEnvironmentVariable("CODEACT_TEST_BWRAP") ?? "/usr/bin/bwrap",
                PythonPath = Environment.GetEnvironmentVariable("CODEACT_TEST_PYTHON") ?? "/opt/openagent-code/venv/bin/python",
                NodePath = Environment.GetEnvironmentVariable("CODEACT_TEST_NODE") ?? "/usr/bin/node",
                TimeoutSeconds = timeoutSeconds,
                MaxSessionSandboxes = maxSessionSandboxes,
                SessionIdleMinutes = sessionIdleMinutes
            });
            var bubblewrap = new BubblewrapProcess(settings, NullLogger<BubblewrapProcess>.Instance);
            Manager = new SessionSandboxManager(bubblewrap, settings, NullLogger<SessionSandboxManager>.Instance);
            Executor = new BubblewrapCodeExecutor(bubblewrap, Manager, settings, NullLogger<BubblewrapCodeExecutor>.Instance);
        }

        internal Task<CodeExecutionResult> ExecuteAsync(string sessionKey, string code) =>
            Executor.ExecuteAsync(new CodeExecutionRequest { Code = code, SessionKey = sessionKey }, CancellationToken.None);

        public async ValueTask DisposeAsync()
        {
            await Manager.ReapIdleAsync(TimeSpan.Zero);
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
