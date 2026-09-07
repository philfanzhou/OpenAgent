using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace OpenAgent.Runner;

/// <summary>Runs the trusted Docker control-plane commands used to start runsc sandboxes.</summary>
internal sealed class DockerProcess(IOptions<RunnerOptions> options, ILogger<DockerProcess> logger)
{
    private int _activeProcesses;

    internal int ActiveProcesses => Volatile.Read(ref _activeProcesses);

    internal Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        IEnumerable<string> arguments, int maxOutputCharacters, CancellationToken cancellationToken) =>
        RunCoreAsync(arguments, maxOutputCharacters, cancellationToken);

    internal async Task<string> CreateAsync(
        IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var result = await RunCoreAsync(arguments, 4096, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Stdout))
        {
            throw new InvalidOperationException($"Docker could not create the gVisor sandbox: {result.Stderr}");
        }
        return result.Stdout.Trim();
    }

    internal async Task<bool> IsAvailableAsync(CancellationToken cancellationToken)
    {
        RunnerOptions settings = options.Value;
        if (!File.Exists(settings.DockerPath))
        {
            RunnerLog.EnvironmentFailed(logger, "health", "prerequisites", "Docker CLI is unavailable.");
            return false;
        }

        try
        {
            var info = await RunCoreAsync(["info", "--format", "{{json .Runtimes}}"], 32 * 1024, cancellationToken)
                .ConfigureAwait(false);
            if (info.ExitCode != 0 || !HasRuntime(info.Stdout, settings.Runtime))
            {
                RunnerLog.EnvironmentFailed(logger, "health", "runtime", $"Docker runtime '{settings.Runtime}' is not registered.");
                return false;
            }

            var image = await RunCoreAsync(["image", "inspect", settings.SandboxImage], 4096, cancellationToken)
                .ConfigureAwait(false);
            if (image.ExitCode != 0)
            {
                RunnerLog.EnvironmentFailed(logger, "health", "image", $"Sandbox image '{settings.SandboxImage}' is unavailable.");
                return false;
            }

            var probe = await RunCoreAsync(
                ["run", "--rm", "--runtime", settings.Runtime, "--network", "none", "--read-only",
                 "--tmpfs", "/tmp:rw,noexec,nosuid,nodev,size=16m", "--entrypoint", "/bin/true", settings.SandboxImage],
                4096, cancellationToken).ConfigureAwait(false);
            if (probe.ExitCode != 0)
            {
                RunnerLog.EnvironmentFailed(logger, "health", "probe", probe.Stderr);
                return false;
            }
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            RunnerLog.EnvironmentFailed(logger, "health", "probe", exception.Message);
            return false;
        }
    }

    internal async Task CopyToAsync(string containerId, string source, string destination,
        CancellationToken cancellationToken)
    {
        // docker cp extracts into the container rootfs and Docker rejects that operation
        // when --read-only is enabled, even when the destination is a mounted tmpfs.
        // Stream the staged bytes through docker exec instead; the sandbox still receives
        // them only in the explicitly mounted /input tmpfs.
        byte[] content = await File.ReadAllBytesAsync(source, cancellationToken).ConfigureAwait(false);
        var result = await RunCoreAsync(
            ["exec", "-i", containerId, "/bin/sh", "-c", $"umask 0222; cat > {destination}"],
            4096, cancellationToken, content).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Docker could not copy an input into the gVisor sandbox: {result.Stderr}");
        }
    }

    internal async Task RemoveAsync(string containerId)
    {
        try
        {
            var result = await RunCoreAsync(["rm", "--force", "--volumes", containerId], 4096, CancellationToken.None)
                .ConfigureAwait(false);
            if (result.ExitCode != 0 && !result.Stderr.Contains("No such container", StringComparison.OrdinalIgnoreCase))
            {
                RunnerLog.CleanupFailed(logger, containerId);
                throw new InvalidOperationException(result.Stderr);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            RunnerLog.CleanupFailed(logger, containerId);
            throw new InvalidOperationException("The gVisor container teardown could not be confirmed.", exception);
        }
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunCoreAsync(
        IEnumerable<string> arguments, int maxOutputCharacters, CancellationToken cancellationToken,
        byte[]? standardInput = null)
    {
        RunnerOptions settings = options.Value;
        var start = new ProcessStartInfo(settings.DockerPath)
        {
            RedirectStandardInput = standardInput is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (!string.IsNullOrWhiteSpace(settings.DockerHost))
        {
            start.Environment["DOCKER_HOST"] = settings.DockerHost;
        }
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
            if (standardInput is not null)
            {
                await process.StandardInput.BaseStream.WriteAsync(standardInput, cancellationToken).ConfigureAwait(false);
                await process.StandardInput.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                process.StandardInput.Close();
            }
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

    private static bool HasRuntime(string output, string runtime)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(output);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.EnumerateObject().Any(item => item.NameEquals(runtime));
        }
        catch (JsonException)
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
