using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using OpenAgent.Runtime.Acp.Protocol;
using OpenAgent.Runtime.Acp.Rpc;
namespace OpenAgent.Runtime.Acp.Client;

/// <summary>Opt-in command execution. The host must explicitly authorize every command.</summary>
public sealed class LocalTerminalHandler(Func<CreateTerminalRequest, bool> authorize, int maxOutputBytes = 64 * 1024, int maxTerminals = 32) : IAcpTerminalHandler, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Terminal> _terminals = [];
    private int _count;
    public Task<CreateTerminalResponse> CreateAsync(CreateTerminalRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!authorize(request)) throw new AcpRpcException(-32000, "Terminal command denied.");
        if (request.Cwd is not null && !Path.IsPathFullyQualified(request.Cwd)) throw new ArgumentException("Working directory must be absolute.");
        ProcessStartInfo start = new(request.Command) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string arg in request.Args ?? []) start.ArgumentList.Add(arg);
        foreach (EnvVariable env in request.Env ?? []) start.Environment[env.Name] = env.Value;
        if (request.Cwd is not null) start.WorkingDirectory = request.Cwd;
        string id = Guid.NewGuid().ToString("N");
        Process process = new() { StartInfo = start };
        if (maxOutputBytes < 1 || maxTerminals < 1) throw new ArgumentOutOfRangeException(nameof(maxOutputBytes));
        if (Interlocked.Increment(ref _count) > maxTerminals) { Interlocked.Decrement(ref _count); process.Dispose(); throw new AcpRpcException(10002, "Terminal limit."); }
        try { process.Start(); } catch { Interlocked.Decrement(ref _count); process.Dispose(); throw; }
        Terminal terminal = new(request.SessionId, process, (int)Math.Min(request.OutputByteLimit ?? (ulong)maxOutputBytes, (ulong)maxOutputBytes));
        _terminals[id] = terminal; return Task.FromResult(new CreateTerminalResponse(id));
    }
    public Task<TerminalOutputResponse> OutputAsync(TerminalRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); Terminal terminal = Get(request);
        lock (terminal.Buffer) return Task.FromResult(new TerminalOutputResponse(terminal.Buffer.ToString(), terminal.Truncated, terminal.Exit.IsCompletedSuccessfully ? terminal.Exit.Result : null));
    }
    public Task<ExitStatus> WaitAsync(TerminalRequest request, CancellationToken ct = default) => Get(request).Exit.WaitAsync(ct);
    public Task<Empty> KillAsync(TerminalRequest request, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); Get(request).Kill(); return Task.FromResult(new Empty()); }
    public async Task<Empty> ReleaseAsync(TerminalRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); Terminal terminal = Get(request);
        if (_terminals.TryRemove(request.TerminalId, out _)) { Interlocked.Decrement(ref _count); await terminal.DisposeAsync().ConfigureAwait(false); }
        return new();
    }
    public async Task CloseSessionAsync(string id)
    {
        foreach (var pair in _terminals.Where(x => x.Value.Session == id).ToArray())
            if (_terminals.TryRemove(pair.Key, out Terminal? terminal)) { Interlocked.Decrement(ref _count); await terminal.DisposeAsync().ConfigureAwait(false); }
    }
    private Terminal Get(TerminalRequest request) => _terminals.TryGetValue(request.TerminalId, out Terminal? terminal) && terminal.Session == request.SessionId
        ? terminal : throw new AcpRpcException(-32002, "Terminal not found.");
    public async ValueTask DisposeAsync() { foreach (string id in _terminals.Values.Select(x => x.Session).Distinct().ToArray()) await CloseSessionAsync(id).ConfigureAwait(false); }
    private sealed class Terminal : IAsyncDisposable
    {
        internal string Session { get; }
        internal StringBuilder Buffer { get; } = new();
        internal bool Truncated;
        internal Task<ExitStatus> Exit { get; }
        private readonly Process _process;
        private readonly int _limit;
        internal Terminal(string session, Process process, int limit)
        { Session = session; _process = process; _limit = limit; Exit = WaitAsync(); }
        private async Task<ExitStatus> WaitAsync()
        {
            Task stdout = ReadAsync(_process.StandardOutput), stderr = ReadAsync(_process.StandardError);
            await _process.WaitForExitAsync().ConfigureAwait(false); await Task.WhenAll(stdout, stderr).ConfigureAwait(false); return new(_process.ExitCode);
        }
        private async Task ReadAsync(StreamReader reader)
        {
            char[] chars = new char[2048]; int count; char? pending = null;
            while ((count = await reader.ReadAsync(chars).ConfigureAwait(false)) != 0)
            {
                string chunk = (pending?.ToString() ?? "") + new string(chars, 0, count);
                pending = char.IsHighSurrogate(chunk[^1]) ? chunk[^1] : null;
                if (pending is not null) chunk = chunk[..^1];
                lock (Buffer)
                {
                    Buffer.Append(chunk);
                    // 按 UTF-8 字节上限保留尾部，并保持代理项/多字节字符边界完整。
                    string text = Buffer.ToString(); int bytes = Encoding.UTF8.GetByteCount(text), offset = 0;
                    while (bytes > _limit && offset < text.Length)
                    { int width = char.IsHighSurrogate(text[offset]) && offset + 1 < text.Length && char.IsLowSurrogate(text[offset + 1]) ? 2 : 1; bytes -= Encoding.UTF8.GetByteCount(text.AsSpan(offset, width)); offset += width; Truncated = true; }
                    if (offset > 0) Buffer.Remove(0, offset);
                }
            }
        }
        internal void Kill() { try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
        public async ValueTask DisposeAsync() { Kill(); await Exit.ConfigureAwait(false); _process.Dispose(); }
    }
}
