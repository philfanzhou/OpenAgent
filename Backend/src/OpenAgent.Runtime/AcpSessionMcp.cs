using OpenAgent.Contracts.Runtime;
using OpenAgent.Runtime.Acp.Protocol;

namespace OpenAgent.Runtime;

/// <summary>把平台解析出的外部 MCP server 定义映射为 ACP session/new 条目；对任何 ACP harness 通用。</summary>
internal static class AcpSessionMcp
{
    internal static McpServer[] Map(IReadOnlyList<RuntimeMcpServer>? servers)
    {
        if (servers is null || servers.Count == 0)
        {
            return [];
        }
        return servers.Select(server => new McpServer(server.Name)
        {
            Type = server.Type,
            Url = server.Url,
            Headers = []
        }).ToArray();
    }
}
