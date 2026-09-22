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
/// 工具并发类别：ReadOnly 可在同一轮内并行执行（仅读取、无可变共享状态）；
/// Exclusive 由执行层用信号量串行化（写入存储、执行代码、MCP 等有副作用调用）。
/// 未显式声明的工具一律按 Exclusive 处理（宁串勿竞）。
/// </summary>
internal enum ToolConcurrency
{
    ReadOnly,
    Exclusive
}

/// <summary>能力工具（AIFunction 包装）向执行层暴露自己声明的并发类别。</summary>
internal interface IToolConcurrencyProvider
{
    ToolConcurrency Concurrency { get; }
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
    string? ParentResourceId = null,
    ToolConcurrency Concurrency = ToolConcurrency.Exclusive);
