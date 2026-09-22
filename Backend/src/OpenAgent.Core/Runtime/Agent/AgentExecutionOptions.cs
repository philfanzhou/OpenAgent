namespace OpenAgent.Core.Runtime.Agent;

public sealed class AgentExecutionOptions
{
    /// <summary>
    /// 单次工具调用的时间上限（秒）；小于等于 0 表示不限时。
    /// 超时的调用会被主动取消，并以带 timedOut 标记的错误结果回传给模型，
    /// 运行继续而不是终止会话。默认对齐 MCP 传输层 HttpClient.Timeout（5 分钟）。
    /// </summary>
    public int ToolCallTimeoutSeconds { get; set; } = 300;
}
