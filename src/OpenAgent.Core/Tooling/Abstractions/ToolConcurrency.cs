using OpenAgent.Contracts.Capabilities;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Core.Tooling.Abstractions;

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
