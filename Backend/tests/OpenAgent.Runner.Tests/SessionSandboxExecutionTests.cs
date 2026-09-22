using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Execution;
using Xunit;

namespace OpenAgent.Runner.Tests;

public class SessionSandboxExecutionTests
{
    [BubblewrapFact]
    public async Task StartDetached_SandboxSurvivesCreatorThreadExit()
    {
        // 回归：bwrap --die-with-parent 的 PR_SET_PDEATHSIG 在"创建者线程"退出时触发。
        // 修复前 fork 发生在可回收的线程池线程上，线程退役即 SIGKILL 存活沙箱；
        // 修复后所有 bwrap fork 都经由专用常驻 launcher 线程，创建者线程退出无影响。
        string root = Path.Combine(Path.GetTempPath(), "codeact-session-tests-" + Guid.NewGuid().ToString("N"));
        string channelDirectory = Path.Combine(root, SessionSandboxManager.SessionDirectoryPrefix + "thread-exit", "channel");
        Directory.CreateDirectory(channelDirectory);
        var settings = Options.Create(new RunnerOptions
        {
            WorkspaceRoot = root,
            BubblewrapPath = Environment.GetEnvironmentVariable("CODEACT_TEST_BWRAP") ?? "/usr/bin/bwrap",
            PythonPath = Environment.GetEnvironmentVariable("CODEACT_TEST_PYTHON") ?? "/opt/openagent-code/venv/bin/python",
            NodePath = Environment.GetEnvironmentVariable("CODEACT_TEST_NODE") ?? "/usr/bin/node"
        });
        var bubblewrap = new BubblewrapProcess(settings, NullLogger<BubblewrapProcess>.Instance);

        Process? process = null;
        var creator = new Thread(() =>
        {
            process = bubblewrap.StartDetached(BubblewrapCodeExecutor.BuildSessionArguments(
                settings.Value, channelDirectory, Path.Combine(AppContext.BaseDirectory, "sandbox")));
            // 给 bwrap 留出完成自身初始化并设置 PDEATHSIG 的时间，随后创建者线程退出。
            Thread.Sleep(TimeSpan.FromMilliseconds(300));
        })
        { IsBackground = true };
        creator.Start();
        creator.Join();

        Assert.NotNull(process);
        try
        {
            for (int i = 0; i < 30 && !File.Exists(Path.Combine(channelDirectory, "supervisor.sock")); i++)
            {
                await Task.Delay(100);
            }
            // PDEATHSIG 若被错误触发（修复前）会在此窗口内以 SIGKILL（exit 137）终结沙箱。
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            Assert.False(process.HasExited);
        }
        finally
        {
            try
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
            catch (Exception)
            {
                // 进程可能已退出；尽力清理即可。
            }
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

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
        CodeExecutionResult setup = await runtime.ExecuteAsync("conv-a",
            "from pathlib import Path\nPath('/work/a.txt').write_text('alpha')");
        Assert.True(setup.ExitCode == 0, setup.Stderr);
        CodeExecutionResult probe = await runtime.ExecuteAsync("conv-b",
            "from pathlib import Path\nassert not Path('/work/a.txt').exists()\nPath('/work/b.txt').write_text('beta')\nprint('isolated')");
        Assert.True(probe.ExitCode == 0, probe.Stderr);
        CodeExecutionResult back = await runtime.ExecuteAsync("conv-a",
            "from pathlib import Path\nassert Path('/work/a.txt').read_text() == 'alpha'\nassert not Path('/work/b.txt').exists()\nprint('own-state')");
        Assert.True(back.ExitCode == 0, back.Stderr + " (sandboxReset=" + back.SandboxReset + ")");
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
    public async Task Execute_SandboxDeathRespawnsSandboxAndPreservesWorkspace()
    {
        await using var runtime = new SessionRuntime();
        await runtime.ExecuteAsync("conv", "from pathlib import Path\nPath('/work/state.txt').write_text('gone-soon')");
        var sandbox = Assert.IsType<SessionSandbox>(runtime.Manager.GetSandbox("conv"));
        sandbox.Process.Kill();
        sandbox.Process.WaitForExit();

        CodeExecutionResult respawned = await runtime.ExecuteAsync("conv", """
            from pathlib import Path
            assert Path('/work/state.txt').read_text() == 'gone-soon'
            print('workspace-survived')
            """);
        Assert.True(respawned.ExitCode == 0, respawned.Stderr);
        // /work 是宿主 bind-mount：沙箱进程死亡重建后状态仍在，不报告 reset。
        Assert.False(respawned.SandboxReset);
        Assert.Equal(1, runtime.Manager.LiveCount);
    }

    [BubblewrapFact]
    public async Task Execute_EvictsAtCapacityAndPreservesWorkspace()
    {
        await using var runtime = new SessionRuntime(maxSessionSandboxes: 1);
        await runtime.ExecuteAsync("alpha", "from pathlib import Path\nPath('/work/a.txt').write_text('a')");
        CodeExecutionResult beta = await runtime.ExecuteAsync("beta", "print('beta')");
        Assert.True(beta.ExitCode == 0, beta.Stderr);
        Assert.Equal(1, runtime.Manager.LiveCount);
        Assert.False(runtime.Manager.IsLive("alpha"));

        CodeExecutionResult alphaAgain = await runtime.ExecuteAsync("alpha",
            "from pathlib import Path\nassert Path('/work/a.txt').read_text() == 'a'\nprint('alpha-back')");
        Assert.True(alphaAgain.ExitCode == 0, alphaAgain.Stderr);
        // 驱逐只回收沙箱进程；宿主工作区存活，不报告 reset。
        Assert.False(alphaAgain.SandboxReset);
    }

    [BubblewrapFact]
    public async Task Execute_ReapReleasesIdleSandboxWithoutWorkspaceReset()
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
        // 释放只回收沙箱进程；工作区目录仍在，不报告 reset。
        Assert.False(after.SandboxReset);
    }

    [BubblewrapFact]
    public async Task Execute_SweptWorkspaceNextRunReportsReset()
    {
        // 后台清扫删除整个会话目录（工作区状态真正丢失）：下一次执行必须以
        // sandboxReset 告知模型此前的文件已不在。
        await using var runtime = new SessionRuntime();
        await runtime.ExecuteAsync("conv", "from pathlib import Path\nPath('/work/state.txt').write_text('swept-soon')");
        runtime.Manager.MarkSwept("conv");
        Directory.Delete(Path.Combine(runtime.Root, "session-conv"), recursive: true);

        CodeExecutionResult after = await runtime.ExecuteAsync("conv", """
            from pathlib import Path
            assert not Path('/work/state.txt').exists()
            print('fresh')
            """);
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
