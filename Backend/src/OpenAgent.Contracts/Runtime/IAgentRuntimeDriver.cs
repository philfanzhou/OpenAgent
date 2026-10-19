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
    IReadOnlyList<RuntimeTool> Tools);

public sealed record RuntimeMessage(
    string Role,
    string Content,
    IReadOnlyList<RuntimeAttachment>? Attachments = null);

public sealed record RuntimeAttachment(string MediaType, ReadOnlyMemory<byte> Data);

/// <summary>工具回调绑定本轮身份；Agent 不能在参数中更换租户或用户。</summary>
public sealed record RuntimeTool(
    string Name,
    string Description,
    string ParametersJsonSchema,
    Func<IReadOnlyDictionary<string, object?>, CancellationToken, Task<string>> InvokeAsync);
