using OpenAgent.Runtime.Acp.Protocol;
using OpenAgent.Runtime.Acp.Rpc;
namespace OpenAgent.Runtime.Acp.Client;
public sealed record AcpClientHandlers
{
    // 扩展方法在 reader 启动前登记；AOT 调用方可显式提供自己的 JsonTypeInfo。
    public Action<AcpRpcConnection>? ConfigureConnection { get; init; }
    public Func<RequestPermissionRequest, CancellationToken, Task<PermissionResponse>>? Permission { get; init; }
    public Func<ReadFileRequest, CancellationToken, Task<ReadFileResponse>>? ReadFile { get; init; }
    public Func<WriteFileRequest, CancellationToken, Task<Empty>>? WriteFile { get; init; }
    public IAcpTerminalHandler? Terminal { get; init; }
    public Func<ElicitationRequest, CancellationToken, Task<ElicitationResponse>>? Elicitation { get; init; }
    public Action<ElicitationComplete>? ElicitationComplete { get; init; }
    public Func<AuthMethod, CancellationToken, Task<int>>? TerminalAuthentication { get; init; }
    public bool FormElicitation { get; init; }
    public bool UrlElicitation { get; init; }
}
public interface IAcpTerminalHandler
{
    Task<CreateTerminalResponse> CreateAsync(CreateTerminalRequest request, CancellationToken cancellationToken);
    Task<TerminalOutputResponse> OutputAsync(TerminalRequest request, CancellationToken cancellationToken);
    Task<ExitStatus> WaitAsync(TerminalRequest request, CancellationToken cancellationToken);
    Task<Empty> KillAsync(TerminalRequest request, CancellationToken cancellationToken);
    Task<Empty> ReleaseAsync(TerminalRequest request, CancellationToken cancellationToken);
    Task CloseSessionAsync(string sessionId);
}
