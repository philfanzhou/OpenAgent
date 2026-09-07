using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Execution;

namespace OpenAgent.Runner;

internal sealed class GVisorCodeExecutor(
    DockerProcess docker, IOptions<RunnerOptions> options, ILogger<GVisorCodeExecutor> logger) : ICodeExecutor
{
    private const int InputMiB = 32;
    private const int OutputMiB = 32;
    private const int TempMiB = 64;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _slots = new(options.Value.MaxConcurrentExecutions);

    public async Task<CodeExecutionResult> ExecuteAsync(CodeExecutionRequest request, CancellationToken cancellationToken)
    {
        ExecutionLimits.Validate(request);
        if (!await _slots.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new RunnerBusyException();
        }

        RunnerOptions settings = options.Value;
        string executionId = Guid.NewGuid().ToString("N");
        string directory = Path.Combine(settings.WorkspaceRoot, executionId);
        string containerName = $"openagent-codeact-{executionId}";
        string? containerId = null;
        Stopwatch watch = Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds + 15));
        RunnerLog.Started(logger, executionId, Activity.Current?.TraceId.ToString() ?? string.Empty);
        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "main.py"), request.Code, deadline.Token).ConfigureAwait(false);
            foreach (ExecutionFile file in request.Files)
            {
                await File.WriteAllBytesAsync(Path.Combine(directory, file.Name), file.Content, deadline.Token).ConfigureAwait(false);
            }

            containerId = await docker.CreateAsync(BuildArguments(settings, containerName), deadline.Token).ConfigureAwait(false);
            var started = await docker.RunAsync(["start", containerId], 4096, deadline.Token)
                .ConfigureAwait(false);
            if (started.ExitCode != 0)
            {
                throw new InvalidOperationException($"Docker could not start the gVisor sandbox: {started.Stderr}");
            }
            await docker.CopyToAsync(containerId, Path.Combine(directory, "main.py"), "/input/main.py", deadline.Token)
                .ConfigureAwait(false);
            foreach (ExecutionFile file in request.Files)
            {
                await docker.CopyToAsync(containerId, Path.Combine(directory, file.Name), $"/input/{file.Name}", deadline.Token)
                    .ConfigureAwait(false);
            }

            var executed = await docker.RunAsync(
                ["exec", containerId, settings.SandboxPythonPath, "-I", "/app/sandbox/execute.py"],
                ExecutionLimits.MaxWireBytes, deadline.Token)
                .ConfigureAwait(false);
            CodeExecutionResult result;
            if (executed.ExitCode != 0)
            {
                RunnerLog.EnvironmentFailed(logger, executionId, "execute", executed.Stderr);
                result = new CodeExecutionResult
                {
                    ExitCode = -1,
                    Stderr = "gVisor sandbox terminated before producing a result (resource limit or process failure)."
                };
            }
            else
            {
                result = JsonSerializer.Deserialize<CodeExecutionResult>(executed.Stdout, JsonOptions)
                    ?? throw new InvalidOperationException("gVisor sandbox result is empty.");
                ExecutionLimits.ValidateFiles(result.Files);
                if (result.Stdout == null || result.Stderr == null
                    || result.Stdout.Length > ExecutionLimits.MaxLogCharacters
                    || result.Stderr.Length > ExecutionLimits.MaxLogCharacters)
                {
                    throw new InvalidOperationException("gVisor sandbox result exceeds the log limit.");
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
                await docker.RemoveAsync(containerId ?? containerName).ConfigureAwait(false);
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (Exception exception)
            {
                RunnerLog.CleanupFailed(logger, executionId);
                throw new InvalidOperationException("gVisor sandbox workspace teardown could not be confirmed.", exception);
            }
            finally
            {
                _slots.Release();
            }
        }
    }

    internal static IReadOnlyList<string> BuildArguments(RunnerOptions settings, string containerName) =>
    [
        "create", "--name", containerName,
        "--runtime", settings.Runtime,
        "--network", "none",
        "--hostname", "openagent-sandbox",
        "--read-only",
        "--user", "65532:65532",
        "--cap-drop", "ALL",
        "--security-opt", "no-new-privileges:true",
        "--pids-limit", settings.MaxProcesses.ToString(CultureInfo.InvariantCulture),
        "--memory", $"{settings.MemoryMiB}m",
        "--cpus", settings.CpuLimit.ToString(CultureInfo.InvariantCulture),
        "--ulimit", $"nproc={settings.MaxProcesses}:{settings.MaxProcesses}",
        "--ulimit", "nofile=256:256",
        "--ulimit", $"fsize={ExecutionLimits.MaxTotalFileBytes}:{ExecutionLimits.MaxTotalFileBytes}",
        "--tmpfs", $"/input:rw,noexec,nosuid,nodev,size={InputMiB}m",
        "--tmpfs", $"/work:rw,noexec,nosuid,nodev,size={settings.WorkspaceMiB}m",
        "--tmpfs", $"/output:rw,nosuid,nodev,size={OutputMiB}m",
        "--tmpfs", $"/tmp:rw,noexec,nosuid,nodev,size={TempMiB}m",
        "--tmpfs", "/run:rw,noexec,nosuid,nodev,size=16m",
        "--workdir", "/work",
        "--env", $"EXECUTION_TIMEOUT={settings.TimeoutSeconds}",
        "--env", "HOME=/tmp/home",
        "--env", "TMPDIR=/tmp",
        "--env", "XDG_RUNTIME_DIR=/tmp/runtime",
        "--env", "LANG=C.UTF-8",
        "--env", "PYTHONDONTWRITEBYTECODE=1",
        "--env", "MPLBACKEND=Agg",
        "--env", "MPLCONFIGDIR=/tmp/matplotlib",
        "--env", "SAL_USE_VCLPLUGIN=svp",
        "--env", "OMP_NUM_THREADS=1",
        "--env", "OPENBLAS_NUM_THREADS=1",
        "--env", "MKL_NUM_THREADS=1",
        "--env", "NUMEXPR_NUM_THREADS=1",
        "--entrypoint", "/bin/sh",
        settings.SandboxImage, "-c", "sleep 900"
    ];
}
