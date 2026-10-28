using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAgent.Contracts.Capabilities;

namespace OpenAgent.Core.Tooling.Discovery;

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

    /// <summary>已激活工具的原始 AITool 视图（注入器每轮合并用）。</summary>
    internal IReadOnlyList<AITool> ActivatedTools()
    {
        lock (_lock)
        {
            return _tools.Where(tool => Activated.Contains(tool.Name)).ToList();
        }
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
