using System.Diagnostics;
using System.Reflection;

namespace OpenAgent.Acp.Demo;

internal sealed class AgentProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Task _stderr;
    private bool _disposed;

    private AgentProcess(Process process)
    {
        _process = process;
        _stderr = DrainErrorsAsync(process.StandardError);
        Transport = new JsonLineTransport(process.StandardOutput.BaseStream, process.StandardInput.BaseStream);
    }

    internal JsonLineTransport Transport { get; }

    internal static AgentProcess Start(string? command = null, IReadOnlyList<string>? arguments = null, string? workingDirectory = null)
    {
        ProcessStartInfo startInfo = new(command ?? Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Directory.GetCurrentDirectory()
        };
        IReadOnlyList<string> processArguments = command is null
            ? [Assembly.GetExecutingAssembly().Location, "agent"]
            : arguments ?? [];
        foreach (string argument in processArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process process = Process.Start(startInfo) ?? throw new IOException("Unable to start the ACP agent.");
        return new AgentProcess(process);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _process.StandardInput.Close();
        bool terminated = false;
        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
            await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                terminated = true;
            }
            await _process.WaitForExitAsync().ConfigureAwait(false);
        }

        await _stderr.ConfigureAwait(false);
        int exitCode = _process.ExitCode;
        await Transport.DisposeAsync().ConfigureAwait(false);
        _process.Dispose();
        if (exitCode != 0 && !terminated)
        {
            throw new IOException($"ACP agent exited with code {exitCode}.");
        }
    }

    private static async Task DrainErrorsAsync(TextReader stderr)
    {
        // Do not retain arbitrary provider logs or authentication information.
        char[] buffer = new char[4096];
        while (await stderr.ReadAsync(buffer).ConfigureAwait(false) != 0)
        {
        }
    }
}
