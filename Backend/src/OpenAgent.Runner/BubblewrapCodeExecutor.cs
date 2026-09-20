using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Execution;

namespace OpenAgent.Runner;

internal sealed class BubblewrapCodeExecutor(
    BubblewrapProcess bubblewrap, SessionSandboxManager sessions, IOptions<RunnerOptions> options,
    ILogger<BubblewrapCodeExecutor> logger) : ICodeExecutor
{
    private const int OutputMiB = 32;
    private const int TempMiB = 64;
    private const int InputMiB = 64;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _slots = new(options.Value.MaxConcurrentExecutions);
    private readonly string _sandboxFilesDirectory = Path.Combine(AppContext.BaseDirectory, "sandbox");

    public async Task<CodeExecutionResult> ExecuteAsync(CodeExecutionRequest request, CancellationToken cancellationToken)
    {
        ExecutionLimits.Validate(request);
        string language = ExecutionLanguage.Normalize(request.Language)
            ?? throw new ArgumentException("The requested execution language is not supported.");
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Bubblewrap code execution requires Linux.");
        }
        if (!await _slots.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new RunnerBusyException();
        }
        string executionId = Guid.NewGuid().ToString("N");
        try
        {
            // Session-keyed requests reuse one persistent sandbox owned by the manager;
            // everything else runs in a fresh sandbox that is torn down immediately.
            return string.IsNullOrWhiteSpace(request.SessionKey)
                ? await ExecuteEphemerallyAsync(request, language, executionId, cancellationToken).ConfigureAwait(false)
                : await ExecuteInSessionAsync(request, executionId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _slots.Release();
        }
    }

    private async Task<CodeExecutionResult> ExecuteInSessionAsync(
        CodeExecutionRequest request, string executionId, CancellationToken cancellationToken)
    {
        RunnerLog.Started(logger, executionId, Activity.Current?.TraceId.ToString() ?? string.Empty);
        Stopwatch watch = Stopwatch.StartNew();
        CodeExecutionResult result = await sessions.ExecuteAsync(request.SessionKey!, request, cancellationToken)
            .ConfigureAwait(false);
        result.ExecutionId = executionId;
        RunnerLog.Completed(logger, executionId, result.ExitCode, watch.ElapsedMilliseconds);
        return result;
    }

    private async Task<CodeExecutionResult> ExecuteEphemerallyAsync(
        CodeExecutionRequest request, string language, string executionId, CancellationToken cancellationToken)
    {
        RunnerOptions settings = options.Value;
        string directory = Path.Combine(settings.WorkspaceRoot, executionId);
        string entryFileName = string.IsNullOrWhiteSpace(request.EntryFileName)
            ? ExecutionLanguage.EntryFileName(language)
            : request.EntryFileName!;
        Stopwatch watch = Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds + 15));
        RunnerLog.Started(logger, executionId, Activity.Current?.TraceId.ToString() ?? string.Empty);
        try
        {
            Directory.CreateDirectory(directory);
            if (OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            await WriteWorkspaceFileAsync(
                Path.Combine(directory, entryFileName),
                Encoding.UTF8.GetBytes(request.Code), deadline.Token).ConfigureAwait(false);
            foreach (ExecutionFile file in request.Files)
            {
                await WriteWorkspaceFileAsync(
                    Path.Combine(directory, file.Name), file.Content, deadline.Token).ConfigureAwait(false);
            }
            foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            var executed = await bubblewrap.RunAsync(
                BuildArguments(settings, directory, _sandboxFilesDirectory, language, entryFileName), ExecutionLimits.MaxWireBytes, deadline.Token)
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
        }
    }

    private static async Task WriteWorkspaceFileAsync(string path, byte[] content, CancellationToken cancellationToken)
    {
        string? parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }
        await File.WriteAllBytesAsync(path, content, cancellationToken).ConfigureAwait(false);
    }

    internal static IReadOnlyList<string> BuildArguments(
        RunnerOptions settings, string inputDirectory, string sandboxFilesDirectory, string language,
        string? entryFileName = null)
    {
        string pythonRoot = RuntimeRoot(settings.PythonPath, "Python");
        string nodeRoot = RuntimeRoot(settings.NodePath, "Node");
        string path = RuntimePath(pythonRoot, nodeRoot);
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
            "--setenv", "PATH", path,
            "--setenv", "HOME", "/tmp/home",
            "--setenv", "TMPDIR", "/tmp",
            "--setenv", "XDG_RUNTIME_DIR", "/tmp/runtime",
            "--setenv", "LANG", "C.UTF-8",
            "--setenv", "PYTHONDONTWRITEBYTECODE", "1",
            "--setenv", "MPLBACKEND", "Agg",
            "--setenv", "MPLCONFIGDIR", "/tmp/matplotlib",
            "--setenv", "SAL_USE_VCLPLUGIN", "svp",
            "--setenv", "OMP_NUM_THREADS", "1",
            "--setenv", "OPENBLAS_NUM_THREADS", "1",
            "--setenv", "MKL_NUM_THREADS", "1",
            "--setenv", "NUMEXPR_NUM_THREADS", "1",
            "--setenv", "EXECUTION_LANGUAGE", language,
            "--setenv", "EXECUTION_NODE", settings.NodePath,
            "--setenv", "EXECUTION_TIMEOUT", settings.TimeoutSeconds.ToString(CultureInfo.InvariantCulture),
            "--setenv", "EXECUTION_ENTRY", entryFileName ?? ExecutionLanguage.EntryFileName(language)
        };

        AddRuntimeBinds(arguments, pythonRoot, nodeRoot);

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

    /// <summary>
    /// Arguments for a persistent session sandbox: same namespaces and hardening as the
    /// ephemeral variant, but with a read-write control channel, a persistent /input
    /// tmpfs and the long-lived supervisor instead of the one-shot entry point.
    /// </summary>
    internal static IReadOnlyList<string> BuildSessionArguments(
        RunnerOptions settings, string channelDirectory, string sandboxFilesDirectory)
    {
        string pythonRoot = RuntimeRoot(settings.PythonPath, "Python");
        string nodeRoot = RuntimeRoot(settings.NodePath, "Node");
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
            "--bind", channelDirectory, "/channel",
            "--ro-bind", sandboxFilesDirectory, "/sandbox",
            "--size", ToBytes(settings.WorkspaceMiB), "--perms", "1777", "--tmpfs", "/work",
            "--size", ToBytes(OutputMiB), "--perms", "1777", "--tmpfs", "/output",
            "--size", ToBytes(TempMiB), "--perms", "1777", "--tmpfs", "/tmp",
            "--size", ToBytes(InputMiB), "--perms", "1777", "--tmpfs", "/input",
            "--perms", "1777", "--tmpfs", "/run",
            "--dir", "/var", "--symlink", "../tmp", "/var/tmp", "--dir", "/home",
            "--proc", "/proc", "--dev", "/dev", "--chdir", "/work",
            "--setenv", "PATH", RuntimePath(pythonRoot, nodeRoot),
            "--setenv", "HOME", "/tmp/home",
            "--setenv", "TMPDIR", "/tmp",
            "--setenv", "XDG_RUNTIME_DIR", "/tmp/runtime",
            "--setenv", "LANG", "C.UTF-8",
            "--setenv", "PYTHONDONTWRITEBYTECODE", "1",
            "--setenv", "MPLBACKEND", "Agg",
            "--setenv", "MPLCONFIGDIR", "/tmp/matplotlib",
            "--setenv", "SAL_USE_VCLPLUGIN", "svp",
            "--setenv", "OMP_NUM_THREADS", "1",
            "--setenv", "OPENBLAS_NUM_THREADS", "1",
            "--setenv", "MKL_NUM_THREADS", "1",
            "--setenv", "NUMEXPR_NUM_THREADS", "1",
            "--setenv", "EXECUTION_NODE", settings.NodePath,
            "--setenv", "SANDBOX_AS_BYTES", ToBytes(settings.MemoryMiB),
            "--setenv", "SANDBOX_NPROC", settings.MaxProcesses.ToString(CultureInfo.InvariantCulture),
            "--setenv", "SANDBOX_NOFILE", "256",
            "--setenv", "SANDBOX_FSIZE", ToBytes(ExecutionLimits.MaxTotalFileBytes / 1024 / 1024),
            "--setenv", "SANDBOX_TIMEOUT", settings.TimeoutSeconds.ToString(CultureInfo.InvariantCulture),
            "--setenv", "SANDBOX_MAX_IDLE_SECONDS",
            (settings.SessionIdleMinutes * 60 + 300).ToString(CultureInfo.InvariantCulture)
        };

        AddRuntimeBinds(arguments, pythonRoot, nodeRoot);
        arguments.AddRange(["--remount-ro", "/"]);
        arguments.AddRange(["--", settings.PythonPath, "-I", "/sandbox/supervisor.py"]);
        return arguments;
    }

    private static string RuntimePath(string pythonRoot, string nodeRoot) =>
        string.Join(":", new[] { pythonRoot, nodeRoot, "/usr" }
            .Distinct(StringComparer.Ordinal)
            .Select(root => root == "/usr" ? "/usr/bin" : root + "/bin")
            .Append("/bin"));

    private static void AddRuntimeBinds(List<string> arguments, string pythonRoot, string nodeRoot)
    {
        foreach (string root in new[] { pythonRoot, nodeRoot }.Distinct(StringComparer.Ordinal))
        {
            if (!root.Equals("/usr", StringComparison.Ordinal)
                && !root.StartsWith("/usr/", StringComparison.Ordinal))
            {
                arguments.AddRange(["--ro-bind", root, root]);
            }
        }
    }

    internal static IReadOnlyList<string> BuildProbeArguments() =>
    [
        "--unshare-user", "--unshare-ipc", "--unshare-pid", "--unshare-net", "--unshare-uts",
        "--unshare-cgroup-try", "--disable-userns", "--new-session", "--die-with-parent",
        "--uid", "65532", "--gid", "65532",
        "--clearenv", "--ro-bind", "/usr", "/usr", "--symlink", "usr/bin", "/bin",
        "--symlink", "usr/lib", "/lib", "--symlink", "usr/lib64", "/lib64",
        "--symlink", "usr/sbin", "/sbin", "--proc", "/proc", "--dev", "/dev", "--", "/bin/true"
    ];

    private static string RuntimeRoot(string executablePath, string name) =>
        Directory.GetParent(Path.GetDirectoryName(executablePath)
            ?? throw new InvalidOperationException($"{name} path has no parent directory."))?.FullName
        ?? throw new InvalidOperationException($"{name} path has no runtime root.");

    private static string ToBytes(int mebibytes) =>
        checked((mebibytes * 1024L * 1024L)).ToString(CultureInfo.InvariantCulture);
}
