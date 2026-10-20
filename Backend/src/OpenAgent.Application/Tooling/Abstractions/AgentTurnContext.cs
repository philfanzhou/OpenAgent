using Microsoft.Extensions.AI;

namespace OpenAgent.Application.Tooling.Abstractions;

/// <summary>
/// 平台自有的轮次上下文：指令、消息与可选的补充工具。MAF 推理栈移除后，
/// 上下文提供器不再依附任何 Agent 框架类型，工具与消息仍复用 M.EAI 基础抽象。
/// </summary>
internal sealed class AgentTurnContext
{
    public string? Instructions { get; set; }
    public List<ChatMessage> Messages { get; } = [];
    public List<AITool>? Tools { get; set; }
}

/// <summary>轮次前/后的上下文钩子；实现方不得执行模型推理。</summary>
internal interface IAgentContextProvider
{
    Task<AgentTurnContext> InvokingAsync(AgentTurnContext context, CancellationToken cancellationToken);

    Task InvokedAsync(AgentTurnContext context, IReadOnlyList<ChatMessage> responses, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
