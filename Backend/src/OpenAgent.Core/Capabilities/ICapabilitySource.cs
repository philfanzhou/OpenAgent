using OpenAgent.Contracts.Capabilities;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Core.Capabilities;

internal interface ICapabilitySource
{
    Task<IReadOnlyList<CapabilityDefinition>> DiscoverAsync(
        string agentId,
        AgentConfig config,
        IAgentUserContext user,
        CancellationToken cancellationToken);
}

/// <summary>
/// 单个工具的完整定义。<see cref="Invoke"/> 返回结构化 <see cref="ToolResult"/>：
/// 错误走统一信封（error/code/hint）而不是抛异常，由执行层渲染后回传模型。
/// </summary>
internal sealed record CapabilityDefinition(
    string Name,
    string Description,
    string ParametersJsonSchema,
    AgentResourceType ResourceType,
    string ResourceId,
    Func<IReadOnlyDictionary<string, object?>, CancellationToken, Task<ToolResult>> Invoke,
    string? ParentResourceId = null);
