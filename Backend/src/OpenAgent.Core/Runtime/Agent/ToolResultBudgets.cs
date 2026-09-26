using OpenAgent.Contracts.Configuration;

namespace OpenAgent.Core.Runtime.Agent;

/// <summary>
/// 单个工具的结果预算：超预算文本做头尾保留截断，并携带该类工具的收窄提示。
/// </summary>
internal readonly record struct ToolResultBudget(int MaxChars, string TruncationHint)
{
    public static ToolResultBudget Unlimited { get; } = new(0, string.Empty);
}

/// <summary>
/// 按工具名把 <see cref="AgentExecutionOptions"/> 的分级预算解析成单工具预算。
/// MCP 命名（mcp__server__tool）由 McpToolFactory 保证；平台工具按白名单归类，
/// 未识别的一律走默认预算，宁紧勿漏。
/// </summary>
internal static class ToolResultBudgets
{
    internal const string ReadHint =
        "extract only the relevant part (e.g. via execute_code with the file mounted at /input) instead of re-reading the whole file";

    internal const string ExecutionHint =
        "print only the relevant part of the output and run again";

    internal const string McpHint =
        "narrow the query or request fewer fields";

    internal const string DefaultHint =
        "reduce the size of the requested output";

    private static readonly HashSet<string> ReadTools = new(StringComparer.Ordinal)
    {
        "read_file",
        "list_files",
        "read_workspace_file",
        "list_workspace_files",
        "search_knowledge_base",
        "get_current_user_profile",
        "load_skill",
        "read_skill_resource",
        "get_context_remaining"
    };

    private static readonly HashSet<string> ExecutionTools = new(StringComparer.Ordinal)
    {
        "execute_code",
        "run_skill_script"
    };

    internal static ToolResultBudget Resolve(AgentExecutionOptions options, string toolName)
    {
        if (toolName.StartsWith("mcp__", StringComparison.Ordinal))
        {
            return new ToolResultBudget(options.McpToolResultCharBudget, McpHint);
        }
        if (ReadTools.Contains(toolName))
        {
            return new ToolResultBudget(options.ReadToolResultCharBudget, ReadHint);
        }
        if (ExecutionTools.Contains(toolName))
        {
            return new ToolResultBudget(options.ExecutionToolResultCharBudget, ExecutionHint);
        }
        return new ToolResultBudget(options.DefaultToolResultCharBudget, DefaultHint);
    }
}
