using System.Diagnostics;
using System.Text;
using OpenAgent.Contracts.Skills;

namespace OpenAgent.SkillSandbox.Host;

internal sealed class ScriptExecutionService(
    SandboxOptions options,
    ILogger<ScriptExecutionService> logger)
{
    private readonly SemaphoreSlim _executionGate = new(1, 1);

    internal async Task<SkillScriptExecutionResult> ExecuteAsync(
        SkillScriptExecutionRequest request,
        CancellationToken cancellationToken)
    {
        Validate(request);
        if (!await _executionGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new SandboxBusyException();
        }

        string workDirectory = Path.Combine(
            Path.GetTempPath(),
            "openagent-sandbox",
            Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(workDirectory);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    workDirectory,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
            }

            string scriptPath = Path.Combine(workDirectory, Path.GetFileName(request.ScriptName));
            await File.WriteAllBytesAsync(scriptPath, request.Script, cancellationToken).ConfigureAwait(false);
            return await RunProcessAsync(
                request.SkillName,
                scriptPath,
                request.Arguments,
                workDirectory,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(workDirectory);
            _executionGate.Release();
        }
    }

    private async Task<SkillScriptExecutionResult> RunProcessAsync(
        string skillName,
        string scriptPath,
        IReadOnlyList<string> arguments,
        string workDirectory,
        CancellationToken cancellationToken)
    {
        using Process process = new()
        {
            StartInfo = CreateStartInfo(scriptPath, arguments, workDirectory)
        };
        Stopwatch stopwatch = Stopwatch.StartNew();
        if (!process.Start())
        {
            throw new InvalidOperationException("The sandbox interpreter could not be started.");
        }

        var outputExceeded = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var outputBudget = new OutputBudget(options.MaxOutputBytes, outputExceeded);
        Task<CapturedOutput> outputTask = ReadLimitedAsync(
            process.StandardOutput.BaseStream,
            outputBudget,
            cancellationToken);
        Task<CapturedOutput> errorTask = ReadLimitedAsync(
            process.StandardError.BaseStream,
            outputBudget,
            cancellationToken);
        Task exitTask = process.WaitForExitAsync(CancellationToken.None);
        Task timeoutTask = Task.Delay(
            TimeSpan.FromSeconds(options.TimeoutSeconds),
            CancellationToken.None);
        Task cancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        Task completed = await Task.WhenAny(
            exitTask,
            timeoutTask,
            outputExceeded.Task,
            cancellationTask).ConfigureAwait(false);

        bool timedOut = completed == timeoutTask;
        bool truncated = completed == outputExceeded.Task;
        if (completed != exitTask)
        {
            TryKill(process);
        }
        await exitTask.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        CapturedOutput output = await outputTask.ConfigureAwait(false);
        CapturedOutput error = await errorTask.ConfigureAwait(false);
        stopwatch.Stop();
        truncated |= output.Truncated || error.Truncated;
        SandboxLog.ScriptCompleted(
            logger,
            skillName,
            Path.GetFileName(scriptPath),
            process.ExitCode,
            stopwatch.ElapsedMilliseconds,
            timedOut,
            truncated);

        return new SkillScriptExecutionResult
        {
            Success = process.ExitCode == 0 && !timedOut && !truncated,
            ExitCode = process.ExitCode,
            StandardOutput = output.Text,
            StandardError = error.Text,
            TimedOut = timedOut,
            OutputTruncated = truncated,
            DurationMs = stopwatch.ElapsedMilliseconds
        };
    }

    private ProcessStartInfo CreateStartInfo(
        string scriptPath,
        IReadOnlyList<string> arguments,
        string workDirectory)
    {
        bool dropUser = !string.IsNullOrWhiteSpace(options.RunAsUser);
        var startInfo = new ProcessStartInfo
        {
            FileName = dropUser ? "/usr/sbin/runuser" : options.Interpreter,
            WorkingDirectory = workDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        if (dropUser)
        {
            startInfo.ArgumentList.Add("--user");
            startInfo.ArgumentList.Add(options.RunAsUser!);
            startInfo.ArgumentList.Add("--");
            startInfo.ArgumentList.Add(options.Interpreter);
        }
        startInfo.ArgumentList.Add("-I");
        startInfo.ArgumentList.Add("-B");
        startInfo.ArgumentList.Add("-s");
        startInfo.ArgumentList.Add(scriptPath);
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment.Clear();
        startInfo.Environment["HOME"] = workDirectory;
        startInfo.Environment["TMPDIR"] = workDirectory;
        startInfo.Environment["LANG"] = "C.UTF-8";
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        startInfo.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        return startInfo;
    }

    private async Task<CapturedOutput> ReadLimitedAsync(
        Stream stream,
        OutputBudget outputBudget,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[4096];
        using var captured = new MemoryStream();
        bool truncated = false;
        while (true)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            int accepted = outputBudget.Claim(read);
            if (accepted > 0)
            {
                captured.Write(buffer, 0, accepted);
            }
            if (accepted < read)
            {
                truncated = true;
                break;
            }
        }

        return new CapturedOutput(
            Encoding.UTF8.GetString(captured.ToArray()),
            truncated);
    }

    private void Validate(SkillScriptExecutionRequest request)
    {
        string fileName = Path.GetFileName(request.ScriptName);
        if (!string.Equals(fileName, request.ScriptName, StringComparison.Ordinal)
            || !options.AllowedExtensions.Contains(
                Path.GetExtension(fileName),
                StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The requested script name or extension is not allowed.");
        }
        if (request.Script.Length == 0 || request.Script.Length > options.MaxScriptBytes)
        {
            throw new InvalidOperationException("The requested script size exceeds the sandbox policy.");
        }
        if (request.Arguments.Count > options.MaxArgumentCount
            || request.Arguments.Any(argument => argument.Length > options.MaxArgumentLength))
        {
            throw new InvalidOperationException("The requested arguments exceed the sandbox policy.");
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record CapturedOutput(string Text, bool Truncated);

    private sealed class OutputBudget(
        int maximumBytes,
        TaskCompletionSource exceeded)
    {
        private int _remainingBytes = maximumBytes;

        internal int Claim(int requestedBytes)
        {
            while (true)
            {
                int remaining = Volatile.Read(ref _remainingBytes);
                int accepted = Math.Min(requestedBytes, Math.Max(remaining, 0));
                if (Interlocked.CompareExchange(
                        ref _remainingBytes,
                        remaining - accepted,
                        remaining) != remaining)
                {
                    continue;
                }
                if (accepted < requestedBytes)
                {
                    exceeded.TrySetResult();
                }
                return accepted;
            }
        }
    }
}

internal sealed class SandboxBusyException : Exception
{
    internal SandboxBusyException()
        : base("The Skill sandbox is already running a script.")
    {
    }
}

internal static partial class SandboxLog
{
    [LoggerMessage(
        EventId = 4200,
        Level = LogLevel.Information,
        Message = "Skill script completed. Skill={SkillName} Script={ScriptName} ExitCode={ExitCode} DurationMs={DurationMs} TimedOut={TimedOut} OutputTruncated={OutputTruncated}")]
    internal static partial void ScriptCompleted(
        ILogger logger,
        string skillName,
        string scriptName,
        int exitCode,
        long durationMs,
        bool timedOut,
        bool outputTruncated);
}
