using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Options;

namespace OpenAgent.Runner;

internal sealed class BubblewrapProcess(IOptions<RunnerOptions> options)
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

        using var process = new Process { StartInfo = start };
        process.Start();
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

    internal async Task<bool> IsAvailableAsync(string sandboxFilesDirectory, CancellationToken cancellationToken)
    {
        RunnerOptions settings = options.Value;
        if (!OperatingSystem.IsLinux()
            || !File.Exists(settings.BubblewrapPath)
            || !File.Exists(settings.PythonPath)
            || !File.Exists("/usr/bin/prlimit")
            || !File.Exists(Path.Combine(sandboxFilesDirectory, "execute.py")))
        {
            return false;
        }

        try
        {
            var probe = await RunAsync(BubblewrapCodeExecutor.BuildProbeArguments(), 4096, cancellationToken)
                .ConfigureAwait(false);
            return probe.ExitCode == 0;
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
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
