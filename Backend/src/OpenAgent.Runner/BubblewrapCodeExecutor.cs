using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Execution;

namespace OpenAgent.Runner;

internal sealed class BubblewrapCodeExecutor(
    BubblewrapProcess bubblewrap, IOptions<RunnerOptions> options, ILogger<BubblewrapCodeExecutor> logger) : ICodeExecutor
{
    private const int OutputMiB = 32;
    private const int TempMiB = 64;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _slots = new(options.Value.MaxConcurrentExecutions);
    private readonly string _sandboxFilesDirectory = Path.Combine(AppContext.BaseDirectory, "sandbox");

    public async Task<CodeExecutionResult> ExecuteAsync(CodeExecutionRequest request, CancellationToken cancellationToken)
    {
        ExecutionLimits.Validate(request);
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Bubblewrap code execution requires Linux.");
        }
        if (!await _slots.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new RunnerBusyException();
        }

        RunnerOptions settings = options.Value;
        string executionId = Guid.NewGuid().ToString("N");
        string directory = Path.Combine(settings.WorkspaceRoot, executionId);
        Stopwatch watch = Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds + 15));
        RunnerLog.Started(logger, executionId, Activity.Current?.TraceId.ToString() ?? string.Empty);
        try
        {
            Directory.CreateDirectory(directory);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            await File.WriteAllTextAsync(Path.Combine(directory, "main.py"), request.Code, deadline.Token).ConfigureAwait(false);
            foreach (ExecutionFile file in request.Files)
            {
                await File.WriteAllBytesAsync(Path.Combine(directory, file.Name), file.Content, deadline.Token).ConfigureAwait(false);
            }
            foreach (string file in Directory.EnumerateFiles(directory))
            {
                File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            var executed = await bubblewrap.RunAsync(
                BuildArguments(settings, directory, _sandboxFilesDirectory), ExecutionLimits.MaxWireBytes, deadline.Token)
                .ConfigureAwait(false);
            CodeExecutionResult result;
            if (executed.ExitCode != 0)
            {
                RunnerLog.EnvironmentFailed(logger, executionId, "execute", executed.Stderr);
                result = new CodeExecutionResult
                {
                    ExitCode = -1,
                    Stderr = "Sandbox terminated before producing a result (resource limit or process failure)."
                };
            }
            else
            {
                result = JsonSerializer.Deserialize<CodeExecutionResult>(executed.Stdout, JsonOptions)
                    ?? throw new InvalidOperationException("Sandbox result is empty.");
                ExecutionLimits.ValidateFiles(result.Files);
                if (result.Stdout == null || result.Stderr == null
                    || result.Stdout.Length > ExecutionLimits.MaxLogCharacters
                    || result.Stderr.Length > ExecutionLimits.MaxLogCharacters)
                {
                    throw new InvalidOperationException("Sandbox result exceeds the log limit.");
                }
            }
            result.ExecutionId = executionId;
            RunnerLog.Completed(logger, executionId, result.ExitCode, watch.ElapsedMilliseconds);
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            RunnerLog.Interrupted(logger, executionId, "deadline");
            return new CodeExecutionResult
            {
                ExecutionId = executionId,
                ExitCode = -1,
                TimedOut = true,
                Stderr = "Execution exceeded its deadline."
            };
        }
        finally
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (Exception)
            {
                RunnerLog.CleanupFailed(logger, executionId);
                throw new InvalidOperationException("Sandbox workspace teardown could not be confirmed; recovery is pending.");
            }
            finally
            {
                _slots.Release();
            }
        }
    }

    internal static IReadOnlyList<string> BuildArguments(
        RunnerOptions settings, string inputDirectory, string sandboxFilesDirectory)
    {
        string pythonRoot = Directory.GetParent(Path.GetDirectoryName(settings.PythonPath)
            ?? throw new InvalidOperationException("Python path has no parent directory."))?.FullName
            ?? throw new InvalidOperationException("Python path has no runtime root.");
        var arguments = new List<string>
        {
            "--unshare-user", "--unshare-ipc", "--unshare-pid", "--unshare-net", "--unshare-uts",
            "--unshare-cgroup-try", "--disable-userns", "--new-session", "--die-with-parent",
            "--uid", "65532", "--gid", "65532", "--hostname", "openagent-sandbox",
            "--clearenv",
            "--ro-bind", "/usr", "/usr",
            "--symlink", "usr/bin", "/bin",
            "--symlink", "usr/lib", "/lib",
            "--symlink", "usr/lib64", "/lib64",
            "--symlink", "usr/sbin", "/sbin",
            "--ro-bind-try", "/etc/fonts", "/etc/fonts",
            "--ro-bind-try", "/etc/libreoffice", "/etc/libreoffice",
            "--ro-bind-try", "/etc/ld.so.cache", "/etc/ld.so.cache",
            "--ro-bind-try", "/etc/localtime", "/etc/localtime",
            "--ro-bind", Path.Combine(sandboxFilesDirectory, "passwd"), "/etc/passwd",
            "--ro-bind", Path.Combine(sandboxFilesDirectory, "group"), "/etc/group",
            "--ro-bind", Path.Combine(sandboxFilesDirectory, "hosts"), "/etc/hosts",
            "--ro-bind", Path.Combine(sandboxFilesDirectory, "nsswitch.conf"), "/etc/nsswitch.conf",
            "--ro-bind", inputDirectory, "/input",
            "--ro-bind", sandboxFilesDirectory, "/sandbox",
            "--size", ToBytes(settings.WorkspaceMiB), "--perms", "1777", "--tmpfs", "/work",
            "--size", ToBytes(OutputMiB), "--perms", "1777", "--tmpfs", "/output",
            "--size", ToBytes(TempMiB), "--perms", "1777", "--tmpfs", "/tmp",
            "--perms", "1777", "--tmpfs", "/run",
            "--dir", "/var", "--symlink", "../tmp", "/var/tmp", "--dir", "/home",
            "--proc", "/proc", "--dev", "/dev", "--chdir", "/work",
            "--setenv", "PATH", $"{pythonRoot}/bin:/usr/bin:/bin",
            "--setenv", "HOME", "/tmp/home",
            "--setenv", "TMPDIR", "/tmp",
            "--setenv", "XDG_RUNTIME_DIR", "/tmp/runtime",
            "--setenv", "LANG", "C.UTF-8",
            "--setenv", "PYTHONDONTWRITEBYTECODE", "1",
            "--setenv", "MPLBACKEND", "Agg",
            "--setenv", "MPLCONFIGDIR", "/tmp/matplotlib",
            "--setenv", "SAL_USE_VCLPLUGIN", "gen",
            "--setenv", "OMP_NUM_THREADS", "1",
            "--setenv", "OPENBLAS_NUM_THREADS", "1",
            "--setenv", "MKL_NUM_THREADS", "1",
            "--setenv", "NUMEXPR_NUM_THREADS", "1",
            "--setenv", "EXECUTION_TIMEOUT", settings.TimeoutSeconds.ToString(CultureInfo.InvariantCulture)
        };

        if (!pythonRoot.Equals("/usr", StringComparison.Ordinal)
            && !pythonRoot.StartsWith("/usr/", StringComparison.Ordinal))
        {
            arguments.AddRange(["--ro-bind", pythonRoot, pythonRoot]);
        }

        // Seal the synthetic root only after every optional parent path has
        // been created by Bubblewrap for the explicit mounts above.
        arguments.AddRange(["--remount-ro", "/"]);

        arguments.AddRange([
            "--", "/usr/bin/prlimit",
            $"--as={ToBytes(settings.MemoryMiB)}:{ToBytes(settings.MemoryMiB)}",
            $"--cpu={settings.TimeoutSeconds + 5}:{settings.TimeoutSeconds + 5}",
            $"--nproc={settings.MaxProcesses}:{settings.MaxProcesses}",
            "--nofile=256:256", $"--fsize={ToBytes(ExecutionLimits.MaxTotalFileBytes / 1024 / 1024)}:{ToBytes(ExecutionLimits.MaxTotalFileBytes / 1024 / 1024)}",
            "--core=0:0", "--", settings.PythonPath, "-I", "/sandbox/execute.py"
        ]);
        return arguments;
    }

    internal static IReadOnlyList<string> BuildProbeArguments() =>
    [
        "--unshare-user", "--unshare-ipc", "--unshare-pid", "--unshare-net", "--unshare-uts",
        "--unshare-cgroup-try", "--disable-userns", "--new-session", "--die-with-parent",
        "--clearenv", "--ro-bind", "/usr", "/usr", "--symlink", "usr/bin", "/bin",
        "--proc", "/proc", "--dev", "/dev", "--", "/bin/true"
    ];

    private static string ToBytes(int mebibytes) =>
        checked((mebibytes * 1024L * 1024L)).ToString(CultureInfo.InvariantCulture);
}
