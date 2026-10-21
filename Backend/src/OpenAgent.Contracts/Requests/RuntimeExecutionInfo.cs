namespace OpenAgent.Contracts.Requests;

/// <summary>实际启动并完成 ACP 握手的运行时身份；不由客户端选择值推断。</summary>
public sealed record RuntimeExecutionInfo(
    string Provider,
    string? ProfileId,
    string? ProfileName,
    string? AgentName,
    string? AgentVersion);
