using OpenAgent.Runtime.Acp.Protocol;
using OpenAgent.Runtime.Acp.Rpc;
namespace OpenAgent.Runtime.Acp.Agent;

public sealed class AcpAgentContext(AcpRpcConnection connection, ClientCapabilities capabilities)
{
    public ClientCapabilities ClientCapabilities { get; } = capabilities;
    public Task UpdateAsync(string sessionId, SessionUpdate update, CancellationToken ct = default)
        => connection.NotifyAsync("session/update", new SessionNotification(sessionId, update), ct);
    public Task<PermissionResponse> RequestPermissionAsync(RequestPermissionRequest request, CancellationToken ct = default)
        => connection.RequestAsync<RequestPermissionRequest, PermissionResponse>("session/request_permission", request, ct, Timeout.InfiniteTimeSpan);
    public Task<ReadFileResponse> ReadFileAsync(ReadFileRequest request, CancellationToken ct = default)
    { Require(ClientCapabilities.Fs?.ReadTextFile == true); return connection.RequestAsync<ReadFileRequest, ReadFileResponse>("fs/read_text_file", request, ct); }
    public Task<Empty> WriteFileAsync(WriteFileRequest request, CancellationToken ct = default)
    { Require(ClientCapabilities.Fs?.WriteTextFile == true); return connection.RequestAsync<WriteFileRequest, Empty>("fs/write_text_file", request, ct); }
    public Task<CreateTerminalResponse> CreateTerminalAsync(CreateTerminalRequest request, CancellationToken ct = default)
    { Require(ClientCapabilities.Terminal); return connection.RequestAsync<CreateTerminalRequest, CreateTerminalResponse>("terminal/create", request, ct); }
    public Task<TerminalOutputResponse> TerminalOutputAsync(TerminalRequest request, CancellationToken ct = default)
    { Require(ClientCapabilities.Terminal); return connection.RequestAsync<TerminalRequest, TerminalOutputResponse>("terminal/output", request, ct); }
    public Task<ExitStatus> WaitForTerminalAsync(TerminalRequest request, CancellationToken ct = default)
    { Require(ClientCapabilities.Terminal); return connection.RequestAsync<TerminalRequest, ExitStatus>("terminal/wait_for_exit", request, ct, Timeout.InfiniteTimeSpan); }
    public Task<Empty> KillTerminalAsync(TerminalRequest request, CancellationToken ct = default)
    { Require(ClientCapabilities.Terminal); return connection.RequestAsync<TerminalRequest, Empty>("terminal/kill", request, ct); }
    public Task<Empty> ReleaseTerminalAsync(TerminalRequest request, CancellationToken ct = default)
    { Require(ClientCapabilities.Terminal); return connection.RequestAsync<TerminalRequest, Empty>("terminal/release", request, ct); }
    public Task<ElicitationResponse> ElicitAsync(ElicitationRequest request, CancellationToken ct = default)
    {
        Require(ClientCapabilities.Elicitation is { } e && e.TryGetProperty(request.Mode, out _));
        return connection.RequestAsync<ElicitationRequest, ElicitationResponse>("elicitation/create", request, ct, Timeout.InfiniteTimeSpan);
    }
    public Task ElicitationCompleteAsync(string id, CancellationToken ct = default)
        => connection.NotifyAsync("elicitation/complete", new ElicitationComplete(id), ct);
    private static void Require(bool enabled) { if (!enabled) throw new NotSupportedException("Client capability not advertised."); }
}
