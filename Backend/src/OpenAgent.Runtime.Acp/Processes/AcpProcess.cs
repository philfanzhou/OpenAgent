using System.Diagnostics;
using OpenAgent.Runtime.Acp.Transport;

namespace OpenAgent.Runtime.Acp.Processes;

public enum AcpEnvironmentMode
{
    Inherit, AllowList
}
public sealed record AcpProcessOptions(string FileName, IReadOnlyList<string> Arguments)
{
    public string? WorkingDirectory
    {
        get; init;
    }
    public AcpEnvironmentMode EnvironmentMode
    {
        get; init;
    }
    public IReadOnlyDictionary<string, string>? Environment
    {
        get; init;
    }
    public TimeSpan ShutdownGracePeriod { get; init; } = TimeSpan.FromSeconds(5);
    public Action<string>? StandardError
    {
        get; init;
    }
    public AcpTransportOptions? Transport
    {
        get; init;
    }
}
/// <summary>拥有自己启动的 Agent 子进程；传输、会话和进程租约分别释放。</summary>
public sealed class AcpProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Task _stderr;
    private readonly AcpProcessOptions _options;
    private readonly object _gate = new();
    private Task? _dispose;
    public IAcpTransport Transport
    {
        get;
    }
    public Task<int> Exit
    {
        get;
    }
    private AcpProcess(Process process, AcpProcessOptions options)
    {
        _process = process;
        _options = options;
        Transport = new AcpStreamTransport(process.StandardOutput.BaseStream, process.StandardInput.BaseStream, options.Transport);
        _stderr = DrainStderrAsync();
        Exit = WaitAsync();
    }
    /// <summary>命令与参数由可信宿主提供，通过 ArgumentList 传递，不经 shell 拼接。</summary>
    public static AcpProcess Start(AcpProcessOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.FileName);
        if (options.WorkingDirectory is not null && !Path.IsPathFullyQualified(options.WorkingDirectory))
        {
            throw new ArgumentException("Working directory must be absolute.");
        }

        ProcessStartInfo start = new(options.FileName)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in options.Arguments)
        {
            start.ArgumentList.Add(argument);
        }

        if (options.WorkingDirectory is not null)
        {
            start.WorkingDirectory = options.WorkingDirectory;
        }

        if (options.EnvironmentMode == AcpEnvironmentMode.AllowList)
        {
            start.Environment.Clear();
        }

        foreach (var pair in options.Environment ?? new Dictionary<string, string>())
        {
            start.Environment[pair.Key] = pair.Value;
        }

        Process process = new()
        {
            StartInfo = start
        };
        try
        {
            if (!process.Start())
            {
                throw new IOException("Could not start Agent.");
            }

            return new(process, options);
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }
    private async Task DrainStderrAsync()
    {
        // 必须持续排空 stderr；不保存无限 transcript，也不让日志回调影响协议进程。
        char[] buffer = new char[2048];
        int count;
        while ((count = await _process.StandardError.ReadAsync(buffer).ConfigureAwait(false)) != 0)
        {
            try
            {
                _options.StandardError?.Invoke(new string(buffer, 0, count));
            }
            catch (Exception)
            {
            }
        }
    }
    private async Task<int> WaitAsync()
    {
        await _process.WaitForExitAsync().ConfigureAwait(false);
        await _stderr.ConfigureAwait(false);
        return _process.ExitCode;
    }
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            return new(_dispose ??= CloseAsync());
        }
    }
    private async Task CloseAsync()
    {
        // 租约只管理本对象创建的进程；关闭单个 session 不释放这个共享租约。
        try
        {
            try
            {
                _process.StandardInput.Close();
            }
            catch (Exception error) when (error is IOException or InvalidOperationException)
            {
            }
            try
            {
                await Exit.WaitAsync(_options.ShutdownGracePeriod).ConfigureAwait(false);
            }
            catch (TimeoutException) { if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                } await Exit.ConfigureAwait(false);
            }
        }
        finally
        {
            try { await Transport.DisposeAsync().ConfigureAwait(false);
            } finally { _process.Dispose();
            }
        }
    }
}
