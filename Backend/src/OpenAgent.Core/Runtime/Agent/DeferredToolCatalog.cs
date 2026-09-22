using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAgent.Contracts.Capabilities;

namespace OpenAgent.Core.Runtime.Agent;

/// <summary>
/// 延迟加载的 MCP 工具目录：工具超过阈值时不整体注入每轮请求（省上下文），
/// 模型经 search_tools 按需检索，检索命中的工具被激活并注入后续请求（对标
/// Codex 的 tool_search + defer_loading）。目录只做名称/描述的静态排序与激活
/// 记账，不做 IO。
/// </summary>
internal sealed class DeferredToolCatalog
{
    private readonly IReadOnlyList<AITool> _tools;
    private readonly object _lock = new();

    internal IReadOnlyList<string> Activated { get; private set; } = [];

    internal DeferredToolCatalog(IReadOnlyList<AITool> tools)
    {
        _tools = tools;
    }

    /// <summary>
    /// 按查询词打分检索：名称命中权重高于描述命中，多词取和；返回前 limit 个
    /// （名称+描述，schema 不随检索返回——激活后才注入完整定义）。
    /// </summary>
    internal IReadOnlyList<(string Name, string Description)> Search(string query, int limit)
    {
        limit = Math.Clamp(limit, 1, 20);
        string[] terms = query
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(term => term.ToLowerInvariant())
            .ToArray();
        List<(string Name, string Description, int Score)> scored = [];
        foreach (AITool tool in _tools)
        {
            string name = tool.Name.ToLowerInvariant();
            string description = (tool.Description ?? string.Empty).ToLowerInvariant();
            int score = 0;
            foreach (string term in terms)
            {
                if (name.Contains(term, StringComparison.Ordinal))
                {
                    score += name.StartsWith(term, StringComparison.Ordinal) ? 5 : 3;
                }
                if (description.Contains(term, StringComparison.Ordinal))
                {
                    score += 1;
                }
            }
            if (score > 0)
            {
                scored.Add((tool.Name, tool.Description ?? string.Empty, score));
            }
        }
        return scored
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .Take(limit)
            .Select(item => (item.Name, item.Description))
            .ToList();
    }

    /// <summary>标记一批工具为已激活（幂等），返回本轮新增激活的原始 AITool。</summary>
    internal IReadOnlyList<AITool> Activate(IEnumerable<string> names)
    {
        HashSet<string> requested = new(names, StringComparer.Ordinal);
        List<AITool> newlyActivated = [];
        lock (_lock)
        {
            HashSet<string> activated = [.. Activated];
            foreach (AITool tool in _tools)
            {
                if (requested.Contains(tool.Name) && activated.Add(tool.Name))
                {
                    newlyActivated.Add(tool);
                }
            }
            Activated = [.. activated];
        }
        return newlyActivated;
    }
}

/// <summary>
/// search_tools 工具本体：检索延迟目录并即时把命中工具（经 wrap 工厂包装后）
/// 注入 ChatOptions.Tools 的共享列表——FICC 在下一轮请求序列化前可见。
/// wrap 工厂与其它工具一致（超时/预算/独占信号量），保证延迟激活的工具不绕过
/// 任何隔离策略。
/// </summary>
internal sealed class ToolSearchFunction : AIFunction
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly DeferredToolCatalog _catalog;
    private readonly List<AITool> _target;
    private readonly Func<AITool, AITool> _wrap;
    private readonly object _lock = new();

    internal ToolSearchFunction(DeferredToolCatalog catalog, List<AITool> target, Func<AITool, AITool> wrap)
    {
        _catalog = catalog;
        _target = target;
        _wrap = wrap;
    }

    public override string Name => "search_tools";

    public override string Description =>
        "Search the deferred tool catalog for external capabilities. This agent's third-party (MCP) tools are not "
        + "listed inline to keep the context small; they live in this catalog instead. "
        + "Call it whenever you need a capability that is not among the visible tools, with short domain keywords "
        + "(e.g. 'calendar event', 'issue tracker'). "
        + "Matching tools become directly callable for the rest of this run — the result lists their names and "
        + "descriptions; call them like any other tool. If nothing matches, rephrase with broader keywords.";

    public override JsonElement JsonSchema { get; } = JsonDocument.Parse(
        """{"type":"object","properties":{"query":{"type":"string","description":"Domain keywords; matched against tool names and descriptions"},"limit":{"type":"number","description":"Maximum tools to return (1..20); defaults to 8"}},"required":["query"],"additionalProperties":false}""")
        .RootElement.Clone();

    protected override ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, object?> values = arguments.ToDictionary(
            item => item.Key,
            item => item.Value);
        string query = values.TryGetValue("query", out object? value)
            ? value?.ToString() ?? string.Empty
            : string.Empty;
        int limit = values.TryGetValue("limit", out object? limitValue)
            && int.TryParse(limitValue?.ToString(), out int parsed)
                ? parsed
                : 8;
        if (string.IsNullOrWhiteSpace(query))
        {
            return ValueTask.FromResult<object?>(ToolResult.Error(
                "'query' is a required argument; provide short domain keywords.",
                "invalid_arguments").Content);
        }

        IReadOnlyList<(string Name, string Description)> matches = _catalog.Search(query, limit);
        if (matches.Count == 0)
        {
            return ValueTask.FromResult<object?>(JsonSerializer.Serialize(new
            {
                tools = Array.Empty<object>(),
                hint = "No deferred tool matched. Try broader keywords, or proceed without the external capability."
            }, JsonOptions));
        }

        List<string> activatedNames = [];
        lock (_lock)
        {
            foreach (AITool tool in _catalog.Activate(matches.Select(match => match.Name)))
            {
                _target.Add(_wrap(tool));
                activatedNames.Add(tool.Name);
            }
        }
        return ValueTask.FromResult<object?>(JsonSerializer.Serialize(new
        {
            tools = matches.Select(match => new { name = match.Name, description = match.Description }).ToArray(),
            newlyActivated = activatedNames,
            hint = "These tools are now callable for the rest of this run; invoke them directly by name."
        }, JsonOptions));
    }
}
