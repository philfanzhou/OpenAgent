namespace OpenAgent.Core.Capabilities.Mcp;

public sealed class McpExecutionOptions
{
    public int ConnectionTimeoutSeconds { get; set; } = 600;

    /// <summary>
    /// 池化 MCP 客户端的空闲淘汰时间（秒）；小于等于 0 表示不淘汰。
    /// 超过该时长未被获取的连接会在下一次获取时被丢弃并重建。
    /// </summary>
    public int ClientIdleTimeoutSeconds { get; set; } = 600;
}
