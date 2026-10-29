using OpenAgent.Contracts.Configuration;
using OpenAgent.Application.Runtime.Agent;

namespace OpenAgent.Application.Tooling.Invocation;

/// <summary>
/// 单个工具的结果预算：超预算文本做头尾保留截断，并携带该类工具的收窄提示。
/// </summary>
internal readonly record struct ToolResultBudget(int MaxChars, string TruncationHint)
{
    public static ToolResultBudget Unlimited { get; } = new(0, string.Empty);
}

/// <summary>
/// 按工具名把 <see cref="AgentExecutionOptions"/> 的分级预算解析成单工具预算。
/// 平台工具按白名单归类，未识别的一律走默认预算，宁紧勿漏；
/// 外部 MCP 工具由 OpenCode 原生连接，不经过本预算机制。
/// </summary>
internal static class ToolResultBudgets
{
    internal const string ReadHint =
        "extract only the relevant part instead of re-reading the whole file";

    internal const string DefaultHint =
        "reduce the size of the requested output";

    private static readonly HashSet<string> ReadTools = new(StringComparer.Ordinal)
    {
        "read_file",
        "list_files",
        "read_skill_file",
        "search_knowledge_base",
        "get_current_user_profile"
    };

    internal static ToolResultBudget Resolve(AgentExecutionOptions options, string toolName)
    {
        if (ReadTools.Contains(toolName))
        {
            return new ToolResultBudget(options.ReadToolResultCharBudget, ReadHint);
        }
        return new ToolResultBudget(options.DefaultToolResultCharBudget, DefaultHint);
    }
}
