namespace OpenAgent.Core.Runtime.Agent;

public sealed class AgentExecutionOptions
{
    /// <summary>
    /// 单次工具调用的时间上限（秒）；小于等于 0 表示不限时。
    /// 超时的调用会被主动取消，并以带 timedOut 标记的错误结果回传给模型，
    /// 运行继续而不是终止会话。默认对齐 MCP 传输层 HttpClient.Timeout（5 分钟）。
    /// </summary>
    public int ToolCallTimeoutSeconds { get; set; } = 300;

    // 工具结果字符预算（约 4 字符 ≈ 1 token），对最终回传模型的文本生效：
    // 超预算的结果做"头尾保留、中间折叠"截断，并附收窄提示引导模型减小下一次请求。
    // 分级对齐主流实现（Claude Code 默认 25k tokens、Codex exec 默认 10k tokens）。
    // 0 或负数表示该级不限。

    /// <summary>读类工具（read_file/list_files/search_knowledge_base 等）结果预算。</summary>
    public int ReadToolResultCharBudget { get; set; } = 80_000;

    /// <summary>执行类工具（execute_code/run_skill_script）结果预算。</summary>
    public int ExecutionToolResultCharBudget { get; set; } = 40_000;

    /// <summary>MCP 工具（mcp__*）结果预算；第三方输出不受控，单独收紧。</summary>
    public int McpToolResultCharBudget { get; set; } = 48_000;

    /// <summary>其余工具结果的默认预算。</summary>
    public int DefaultToolResultCharBudget { get; set; } = 60_000;
}
