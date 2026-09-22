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

/// <summary>
/// search_tools 工具本体：检索延迟目录并登记激活。真正的注入发生在请求边界
/// （<see cref="DeferredToolInjector"/>）——向 ChatOptions.Tools 共享列表追加对
/// MAF 的每轮 options 克隆不可见，必须逐轮合并。
/// </summary>
internal sealed class ToolSearchFunction : AIFunction
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly DeferredToolCatalog _catalog;

    internal ToolSearchFunction(DeferredToolCatalog catalog)
    {
        _catalog = catalog;
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

        List<string> activatedNames = _catalog.Activate(matches.Select(match => match.Name))
            .Select(tool => tool.Name)
            .ToList();
        return ValueTask.FromResult<object?>(JsonSerializer.Serialize(new
        {
            tools = matches.Select(match => new { name = match.Name, description = match.Description }).ToArray(),
            newlyActivated = activatedNames,
            hint = "These tools are now callable for the rest of this run; invoke them directly by name."
        }, JsonOptions));
    }
}

/// <summary>
/// 请求边界的激活工具注入器：包在 FunctionInvokingChatClient 外层，把已激活的
/// 延迟工具（经 wrap 工厂包装，超时/预算/独占信号量与内联工具一致）合并进
/// 当轮 options.Tools——既影响发往 provider 的定义序列化，也让 FICC 能解析
/// 模型对这些工具的调用。幂等：已在列表中的不重复追加。
/// </summary>
internal sealed class DeferredToolInjector(
    IChatClient inner,
    DeferredToolCatalog catalog,
    Func<AITool, AITool> wrap) : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        inner.GetResponseAsync(messages, WithActivatedTools(options), cancellationToken);

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        inner.GetStreamingResponseAsync(messages, WithActivatedTools(options), cancellationToken);

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        inner.GetService(serviceType, serviceKey);

    public void Dispose() => inner.Dispose();

    private ChatOptions? WithActivatedTools(ChatOptions? options)
    {
        IReadOnlyList<string> activated = catalog.Activated;
        if (activated.Count == 0 || options == null)
        {
            return options;
        }
        List<AITool> tools = [.. (options.Tools ?? [])];
        HashSet<string> present = new(tools.Select(tool => tool.Name), StringComparer.Ordinal);
        foreach (AITool tool in catalog.ActivatedTools())
        {
            if (present.Add(tool.Name))
            {
                tools.Add(wrap(tool));
            }
        }
        options.Tools = tools;
        return options;
    }
}
