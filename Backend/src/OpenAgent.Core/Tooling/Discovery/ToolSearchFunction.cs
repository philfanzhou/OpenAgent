using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAgent.Contracts.Capabilities;

namespace OpenAgent.Core.Tooling.Discovery;

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
