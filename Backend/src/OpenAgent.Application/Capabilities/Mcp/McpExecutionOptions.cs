namespace OpenAgent.Application.Capabilities.Mcp;

public sealed class McpExecutionOptions
{
    /// <summary>管理与测试连接用途的 MCP 连接超时（秒）。</summary>
    public int ConnectionTimeoutSeconds { get; set; } = 600;
}
