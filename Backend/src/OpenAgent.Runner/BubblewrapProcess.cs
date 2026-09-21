using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Options;

namespace OpenAgent.Runner;

internal sealed class BubblewrapProcess(IOptions<RunnerOptions> options, ILogger<BubblewrapProcess> logger)
{
    private int _activeProcesses;

    internal int ActiveProcesses => Volatile.Read(ref _activeProcesses);

    internal async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        IEnumerable<string> arguments, int maxOutputCharacters, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(options.Value.BubblewrapPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = BwrapLauncher.Start(start);
        Interlocked.Increment(ref _activeProcesses);
        Task<string> stdout = ReadBoundedAsync(process.StandardOutput, maxOutputCharacters);
        Task<string> stderr = ReadBoundedAsync(process.StandardError, 4096);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return (process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            throw;
        }
        finally
        {
            Interlocked.Decrement(ref _activeProcesses);
        }
    }

    /// <summary>
    /// Starts a Bubblewrap sandbox the caller keeps alive (session sandboxes); its
    /// stderr flows to the service journal and it must be disposed by the caller.
    /// </summary>
    internal Process StartDetached(IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(options.Value.BubblewrapPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        return BwrapLauncher.Start(start);
    }

    /// <summary>
    /// bwrap 通过 PR_SET_PDEATHSIG 实现 --die-with-parent，而该信号在创建子进程的
    /// "父线程"退出时触发——并非父进程退出。若 fork 发生在线程池线程上，线程回收
    /// 会立即 SIGKILL 尚在存活的沙箱（长寿命会话沙箱被随机击杀、状态丢失）。
    /// 所有 bwrap fork 固定路由到这条专用常驻线程：该线程不退役，die-with-parent
    /// 语义即回归"宿主进程退出"；进程退出时后台线程随之销毁，孤儿沙箱仍会被清理。
    /// </summary>
    private static class BwrapLauncher
    {
        private static readonly BlockingCollection<Action> Work = [];

        static BwrapLauncher()
        {
            var thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = "bwrap-launcher"
            };
            thread.Start();
        }

        internal static Process Start(ProcessStartInfo start)
        {
            using var done = new ManualResetEventSlim(false);
            Process? started = null;
            Exception? failure = null;
            Work.Add(() =>
            {
                try
                {
                    var process = new Process { StartInfo = start };
                    process.Start();
                    Volatile.Write(ref started, process);
                }
                catch (Exception exception)
                {
                    Volatile.Write(ref failure, exception);
                }
                finally
                {
                    done.Set();
                }
            });
            done.Wait();
            return Volatile.Read(ref started)
                ?? throw new InvalidOperationException("Failed to start the sandbox process.", Volatile.Read(ref failure));
        }

        private static void Loop()
        {
            foreach (Action work in Work.GetConsumingEnumerable())
            {
                work();
            }
        }
    }

    internal async Task<bool> IsAvailableAsync(string sandboxFilesDirectory, CancellationToken cancellationToken)    {
        RunnerOptions settings = options.Value;
        if (!OperatingSystem.IsLinux()
            || !File.Exists(settings.BubblewrapPath)
            || !File.Exists(settings.PythonPath)
            || !File.Exists(settings.NodePath)
            || !File.Exists("/usr/bin/prlimit")
            || !File.Exists(Path.Combine(sandboxFilesDirectory, "execute.py"))
            || !File.Exists(Path.Combine(sandboxFilesDirectory, "supervisor.py"))
            || !File.Exists(Path.Combine(sandboxFilesDirectory, "execution_core.py")))
        {
            RunnerLog.EnvironmentFailed(logger, "health", "prerequisites", "Linux, bwrap, Python, Node, prlimit, or sandbox entry points are unavailable.");
            return false;
        }

        try
        {
            var probe = await RunAsync(BubblewrapCodeExecutor.BuildProbeArguments(), 4096, cancellationToken)
                .ConfigureAwait(false);
            if (probe.ExitCode != 0)
            {
                RunnerLog.EnvironmentFailed(logger, "health", "probe", probe.Stderr);
            }
            return probe.ExitCode == 0;
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            RunnerLog.EnvironmentFailed(logger, "health", "probe", exception.Message);
            return false;
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int limit)
    {
        var output = new StringBuilder();
        char[] buffer = new char[8192];
        int count;
        while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            output.Append(buffer, 0, Math.Min(count, Math.Max(0, limit - output.Length)));
        }
        return output.ToString();
    }
}
