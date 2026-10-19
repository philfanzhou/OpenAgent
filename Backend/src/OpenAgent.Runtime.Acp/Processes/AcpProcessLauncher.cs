using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using OpenAgent.Runtime.Acp.Transport;

namespace OpenAgent.Runtime.Acp.Processes;

/// <summary>
/// Starts a local agent process and returns its framed transport with an owning
/// <see cref="AcpProcessLease"/>. The launcher never composes a shell command: the child
/// runs from <c>executable</c> plus one argument list element per
/// <see cref="AcpProcessOptions.Arguments"/> entry. Starting does not speak ACP — the
/// handshake stays an explicit role-layer call (SPEC: no implicit initialize).
/// </summary>
public static class AcpProcessLauncher
{
    /// <summary>Starts the executable and wires its stdio into a bounded framed transport.</summary>
    /// <exception cref="AcpProcessStartException">The platform refused or failed to start the executable; nothing is left running.</exception>
    public static async Task<AcpProcessLease> StartAsync(AcpProcessOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Executable);
        if (options.Arguments.Any(static argument => argument is null))
        {
            throw new ArgumentException("Arguments must not contain null entries.", nameof(options));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(options.ShutdownGracePeriod, TimeSpan.Zero);
        if (options.WorkingDirectory is { } workingDirectory && !Directory.Exists(workingDirectory))
        {
            // The platform error for a missing cwd would blame the executable instead.
            throw new ArgumentException($"The working directory '{workingDirectory}' does not exist.", nameof(options));
        }
        if (options.Environment is not null)
        {
            foreach (KeyValuePair<string, string> entry in options.Environment)
            {
                if (string.IsNullOrEmpty(entry.Key) || entry.Value is null)
                {
                    throw new ArgumentException("Environment entries need a nonempty key and a non-null value.", nameof(options));
                }
            }
        }

        ProcessStartInfo start = new(options.Executable)
        {
            UseShellExecute = false,
            WorkingDirectory = options.WorkingDirectory,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
            RedirectStandardError = true
        };
        foreach (string argument in options.Arguments)
        {
            start.ArgumentList.Add(argument);
        }

        if (options.EnvironmentMode == AcpEnvironmentMode.AllowList)
        {
            // Start from a cleared environment; the OS may still inject a few launcher-level
            // variables, but nothing from this process survives the clear.
            foreach (string name in start.Environment.Keys.ToArray())
            {
                start.Environment.Remove(name);
            }
        }

        if (options.Environment is not null)
        {
            foreach (KeyValuePair<string, string> entry in options.Environment)
            {
                start.Environment[entry.Key] = entry.Value;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException("The platform returned no process.");
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            throw new AcpProcessStartException(options.Executable, exception);
        }

        AcpStreamTransport transport;
        Task stderrPump;
        try
        {
            transport = new AcpStreamTransport(
                process.StandardOutput.BaseStream, process.StandardInput.BaseStream, options.Transport);
            stderrPump = PumpStdErrorAsync(process.StandardError, options.OnStdErrorLine);
        }
        catch (Exception exception)
        {
            // Nothing may own the child yet: reclaim it before surfacing the failure.
            TryKill(process);
            throw new AcpProcessStartException(options.Executable, exception);
        }

        return new AcpProcessLease(process, transport, options, stderrPump);
    }

    /// <summary>Continuously drains stderr so a chatty child can never block on a full pipe.</summary>
    private static async Task PumpStdErrorAsync(StreamReader reader, Action<string>? onLine)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                try
                {
                    onLine?.Invoke(line);
                }
                catch
                {
                    // A throwing callback must not end the drain; the child would stall.
                }
            }
        }
        catch
        {
            // The pipe dies with the process or during teardown; the exit record carries the rest.
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // The child never started or already exited; there is nothing to reclaim.
        }

        process.Dispose();
    }
}
