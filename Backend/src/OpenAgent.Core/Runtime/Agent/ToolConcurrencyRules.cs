using Microsoft.Extensions.AI;
using OpenAgent.Core.Capabilities;

namespace OpenAgent.Core.Runtime.Agent;

/// <summary>
/// 把工具解析成并发类别。能力源声明的 <see cref="ToolConcurrency"/> 优先；
/// 非能力工具（MCP/MAF Skill）按名称保守归类：一律 Exclusive（MCP 有外部副作用、
/// skill 读取共享目录，均不保证无状态），未识别工具同样落 Exclusive。
/// </summary>
internal static class ToolConcurrencyRules
{
    internal static ToolConcurrency Resolve(AITool tool) =>
        tool switch
        {
            IToolConcurrencyProvider declared => declared.Concurrency,
            _ => ToolConcurrency.Exclusive
        };
}
