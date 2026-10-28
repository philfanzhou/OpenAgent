namespace OpenAgent.Core.Capabilities.Mcp;

public sealed class McpExecutionOptions
{
    public int ConnectionTimeoutSeconds { get; set; } = 600;

    /// <summary>
    /// 池化 MCP 客户端的空闲淘汰时间（秒）；小于等于 0 表示不淘汰。
    /// 超过该时长未被获取的连接会在下一次获取时被丢弃并重建。
    /// </summary>
    public int ClientIdleTimeoutSeconds { get; set; } = 600;

    /// <summary>
    /// MCP 工具延迟加载阈值：可见 MCP 工具数超过该值时不整体注入请求，
    /// 改为单个 search_tools 入口按需激活（对标 Codex defer_loading）。
    /// 小于等于 0 表示永不延迟（全部内联）。
    /// </summary>
    public int DeferredToolThreshold { get; set; } = 20;
}
