using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Execution;

namespace OpenAgent.Runner;

/// <summary>Thrown when the sandbox process or its supervisor socket is gone mid-request.</summary>
internal sealed class SessionSandboxUnavailableException : Exception
{
    public SessionSandboxUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>One persistent Bubblewrap sandbox owned by a conversation session key.</summary>
internal sealed class SessionSandbox : ISessionSandbox
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ILogger _logger;
    private readonly RunnerOptions _settings;
    private readonly Process _process;
    private readonly string _directory;
    private readonly string _socketPath;
    private long _lastUsedUtcTicks = DateTimeOffset.UtcNow.UtcTicks;
    private bool _released;

    private SessionSandbox(RunnerOptions settings, ILogger logger, string sessionKey,
        Process process, string directory, string socketPath, bool recovered)
    {
        _settings = settings;
        _logger = logger;
        SessionKey = sessionKey;
        _process = process;
        _directory = directory;
        _socketPath = socketPath;
        Recovered = recovered;
    }

    public string SessionKey { get; }

    /// <summary>Host-side bwrap handle; exposed for tests that simulate sandbox death.</summary>
    internal Process Process => _process;

    public DateTimeOffset LastUsedUtc => new(Volatile.Read(ref _lastUsedUtcTicks), TimeSpan.Zero);

    public bool IsAlive => !_process.HasExited;

    /// <summary>True when a sandbox directory already existed at spawn time, meaning an
    /// earlier sandbox for this session was reclaimed and its state is gone.</summary>
    public bool Recovered { get; }

    /// <summary>Starts the sandbox and waits for its supervisor socket to become connectable.</summary>
    public static async Task<SessionSandbox> SpawnAsync(BubblewrapProcess bubblewrap, RunnerOptions settings,
        ILogger logger, string sessionKey, string sandboxFilesDirectory)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Bubblewrap code execution requires Linux.");
        }
        string directory = Path.Combine(settings.WorkspaceRoot,
            SessionSandboxManager.SessionDirectoryPrefix + sessionKey);
        bool recovered = Directory.Exists(directory);
        string channelDirectory = Path.Combine(directory, "channel");
        Directory.CreateDirectory(channelDirectory);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(channelDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        string socketPath = Path.Combine(channelDirectory, "supervisor.sock");
        Process process = bubblewrap.StartDetached(
            BubblewrapCodeExecutor.BuildSessionArguments(settings, channelDirectory, sandboxFilesDirectory));
        try
        {
            using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (!File.Exists(socketPath))
            {
                if (process.HasExited)
                {
                    throw new InvalidOperationException("The session sandbox exited during startup.");
                }
                try
                {
                    await Task.Delay(100, startup.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw new InvalidOperationException("The session sandbox did not become ready in time.");
                }
            }
        }
        catch
        {
            TryKill(process);
            TryDelete(channelDirectory);
            throw;
        }
        RunnerLog.SandboxSpawned(logger, sessionKey);
        return new SessionSandbox(settings, logger, sessionKey, process, directory, socketPath, recovered);
    }

    public async Task<CodeExecutionResult> ExecuteAsync(CodeExecutionRequest request, CancellationToken cancellationToken)
    {
        Volatile.Write(ref _lastUsedUtcTicks, DateTimeOffset.UtcNow.UtcTicks);
        var wire = new SessionWireRequest
        {
            Code = request.Code,
            Language = ExecutionLanguage.Normalize(request.Language) ?? request.Language,
            Entry = request.EntryFileName,
            Files = request.Files.Select(file => new SessionWireFile
            {
                Name = file.Name,
                Content = Convert.ToBase64String(file.Content)
            }).ToList()
        };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(_settings.TimeoutSeconds + 15));
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(_socketPath), deadline.Token).ConfigureAwait(false);
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(wire, JsonOptions);
            Array.Resize(ref payload, payload.Length + 1);
            payload[^1] = (byte)'\n';
            await socket.SendAsync(payload, SocketFlags.None, deadline.Token).ConfigureAwait(false);
            SessionWireResult? result = await ReadResultAsync(socket, deadline.Token).ConfigureAwait(false);
            CodeExecutionResult mapped = MapResult(result);
            Volatile.Write(ref _lastUsedUtcTicks, DateTimeOffset.UtcNow.UtcTicks);
            return mapped;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The deadline fired: the supervisor is wedged, so tear the sandbox down
            // instead of returning it to the registry still alive.
            await ReleaseAsync("deadline").ConfigureAwait(false);
            return new CodeExecutionResult
            {
                ExitCode = -1,
                TimedOut = true,
                Stderr = "Execution exceeded its deadline."
            };
        }
        catch (Exception exception) when (exception is SocketException or IOException or JsonException)
        {
            throw new SessionSandboxUnavailableException(
                "The session sandbox supervisor is unreachable.", exception);
        }
    }

    private static async Task<SessionWireResult?> ReadResultAsync(Socket socket, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[65536];
        while (true)
        {
            int received = await socket.ReceiveAsync(chunk, SocketFlags.None, cancellationToken).ConfigureAwait(false);
            if (received == 0)
            {
                break;
            }
            if (buffer.Length + received > ExecutionLimits.MaxWireBytes)
            {
                throw new InvalidOperationException("The session sandbox response exceeds the wire limit.");
            }
            int newline = Array.IndexOf(chunk, (byte)'\n', 0, received);
            if (newline >= 0)
            {
                buffer.Write(chunk, 0, newline);
                break;
            }
            buffer.Write(chunk, 0, received);
        }
        return JsonSerializer.Deserialize<SessionWireResult>(buffer.ToArray(), JsonOptions);
    }

    private static CodeExecutionResult MapResult(SessionWireResult? wire)
    {
        if (wire is null)
        {
            throw new SessionSandboxUnavailableException("The session sandbox returned an empty result.");
        }
        try
        {
            var result = new CodeExecutionResult
            {
                ExitCode = wire.ExitCode,
                TimedOut = wire.TimedOut,
                Stdout = wire.Stdout ?? string.Empty,
                Stderr = wire.Stderr ?? string.Empty,
                Files = wire.Files?.Select(file => new ExecutionFile
                {
                    Name = file.Name,
                    Content = Convert.FromBase64String(file.Content ?? string.Empty)
                }).ToList() ?? []
            };
            ExecutionLimits.ValidateFiles(result.Files);
            if (result.Stdout.Length > ExecutionLimits.MaxLogCharacters
                || result.Stderr.Length > ExecutionLimits.MaxLogCharacters)
            {
                throw new InvalidOperationException("The session sandbox result exceeds the log limit.");
            }
            return result;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            throw new InvalidOperationException(
                "The session sandbox returned an invalid result.", exception);
        }
    }

    public async ValueTask ReleaseAsync(string reason)
    {
        if (_released)
        {
            return;
        }
        _released = true;
        TryKill(_process);
        try
        {
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The kill signal is already out; the reaper sweep removes leftovers.
        }
        // Keep the session directory behind as a "state was reclaimed" marker so the
        // next spawn reports SandboxReset; the workspace sweep removes it later.
        TryDelete(Path.Combine(_directory, "channel"));
        RunnerLog.SandboxReleased(_logger, SessionKey, reason);
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
        catch (Exception)
        {
            // Already gone; nothing else to do.
        }
    }

    private static void TryDelete(string directory)
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
            // The workspace sweep removes leftovers on its next pass.
        }
    }

    private sealed class SessionWireRequest
    {
        public string Code { get; init; } = string.Empty;
        public string Language { get; init; } = string.Empty;
        public string? Entry { get; init; }
        public List<SessionWireFile> Files { get; init; } = [];
    }

    private sealed class SessionWireFile
    {
        public string Name { get; init; } = string.Empty;
        public string? Content { get; init; }
    }

    private sealed class SessionWireResult
    {
        public int ExitCode { get; init; }
        public bool TimedOut { get; init; }
        public string? Stdout { get; init; }
        public string? Stderr { get; init; }
        public List<SessionWireFile>? Files { get; init; }
    }
}
