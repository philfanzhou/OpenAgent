using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Requests;

namespace OpenAgent.Contracts.Runtime;

/// <summary>推理运行时边界；平台负责已授权的输入、工具和持久化，驱动不暴露协议类型。</summary>
public interface IAgentRuntimeDriver
{
    IAsyncEnumerable<AgentStreamEvent> RunAsync(
        RuntimeInvocation invocation,
        CancellationToken cancellationToken = default);
}

public sealed record RuntimeInvocation(
    AgentRuntimeProfile Profile,
    TurnContext Turn,
    string Instructions,
    IReadOnlyList<RuntimeMessage> Messages,
    IReadOnlyList<RuntimeTool> Tools,
    IReadOnlyList<RuntimeSkill>? Skills = null,
    IReadOnlyList<RuntimeMcpServer>? McpServers = null)
{
    /// <summary>平台已提交历史的版本与上下文代次；缺省时不续用 harness 会话。</summary>
    public string? HistoryCheckpoint { get; init; }

    /// <summary>终态事件被消费后提交平台历史，返回新的检查点；失败必须丢弃运行时状态。</summary>
    public Func<CancellationToken, Task<string?>>? CommitAsync { get; init; }

    /// <summary>Bind the turn's platform file tools after acquiring the workspace, before exposing the MCP bridge.</summary>
    public Func<IRuntimeWorkspaceFiles, CancellationToken, Task<RuntimeFilePreparation>>? PrepareFilesAsync { get; init; }
}

public sealed record RuntimeMessage(
    string Role,
    string Content,
    IReadOnlyList<RuntimeAttachment>? Attachments = null);

public sealed record RuntimeAttachment(string MediaType, ReadOnlyMemory<byte> Data);

/// <summary>已物化的 Skill 包目录；已验证的内容哈希用于复用工作区。</summary>
public sealed record RuntimeSkill(string Name, string Path)
{
    public string? ContentHash { get; init; }
}

/// <summary>
/// 按 Agent 配置启用的外部 MCP server 定义；驱动经 ACP session/new 直接透传给
/// harness 原生连接，不经平台代理工具转发。
/// </summary>
public sealed record RuntimeMcpServer(string Name, string Url, string Type);

/// <summary>工具回调绑定本轮身份；Agent 不能在参数中更换租户或用户。</summary>
public sealed record RuntimeTool(
    string Name,
    string Description,
    string ParametersJsonSchema,
    Func<IReadOnlyDictionary<string, object?>, CancellationToken, Task<string>> InvokeAsync);
