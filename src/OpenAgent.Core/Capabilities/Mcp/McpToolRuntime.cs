using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Core.Capabilities.Mcp;

internal sealed class McpToolRuntime(
    IReadOnlyList<AITool> tools,
    AITool? resourceReader = null) : IAsyncDisposable
{
    internal static McpToolRuntime Empty { get; } = new([]);

    internal IReadOnlyList<AITool> Tools { get; } = tools;

    /// <summary>read_mcp_resource 桥接工具；无可见 MCP 服务器时为 null。</summary>
    internal AITool? ResourceReader { get; } = resourceReader;

    // 连接由 McpClientPool 持有并跨轮次复用；运行时本身没有需要释放的资源。
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
