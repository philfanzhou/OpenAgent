using OpenAgent.Runtime.Acp.Protocol;
using OpenAgent.Runtime.Acp.Rpc;
namespace OpenAgent.Runtime.Acp.Agent;

public sealed record AcpAgentHandlers
{
    // 扩展方法在 reader 启动前登记；AOT 调用方可显式提供自己的 JsonTypeInfo。
    public Action<AcpRpcConnection>? ConfigureConnection { get; init; }
    public required Func<InitializeRequest, CancellationToken, Task<InitializeResponse>> Initialize { get; init; }
    public required Func<NewSessionRequest, AcpAgentContext, CancellationToken, Task<SessionResponse>> NewSession { get; init; }
    public required Func<PromptRequest, AcpAgentContext, CancellationToken, Task<PromptResponse>> Prompt { get; init; }
    public Func<LoadSessionRequest, AcpAgentContext, CancellationToken, Task<SessionResponse>>? Load { get; init; }
    public Func<LoadSessionRequest, AcpAgentContext, CancellationToken, Task<SessionResponse>>? Resume { get; init; }
    public Func<ListSessionsRequest, CancellationToken, Task<ListSessionsResponse>>? List { get; init; }
    public Func<SessionRequest, CancellationToken, Task<Empty>>? Close { get; init; }
    public Func<SessionRequest, CancellationToken, Task<Empty>>? Delete { get; init; }
    public Func<SetModeRequest, CancellationToken, Task<Empty>>? SetMode { get; init; }
    public Func<SetConfigRequest, CancellationToken, Task<SetConfigResponse>>? SetConfig { get; init; }
    public Func<AuthenticateRequest, CancellationToken, Task<Empty>>? Authenticate { get; init; }
    public Func<Empty, CancellationToken, Task<Empty>>? Logout { get; init; }
}
