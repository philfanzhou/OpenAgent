using ModelContextProtocol.Client;
using OpenAgent.Contracts.Mcp;

namespace OpenAgent.Core.Capabilities.Mcp;

internal sealed class McpToolCatalog
{
    private readonly List<McpTool> _tools = [];

    internal void Replace(IEnumerable<McpClientTool> tools)
    {
        Replace(tools.Select(Map));
    }

    internal void Replace(IEnumerable<McpTool> tools)
    {
        lock (_tools)
        {
            _tools.Clear();
            _tools.AddRange(tools);
        }
    }

    internal List<McpTool> List()
    {
        lock (_tools)
        {
            return _tools.ToList();
        }
    }

    internal McpTool? Find(string name)
    {
        lock (_tools)
        {
            return _tools.FirstOrDefault(tool =>
                tool.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        }
    }

    internal void Clear()
    {
        lock (_tools)
        {
            _tools.Clear();
        }
    }

    private static McpTool Map(McpClientTool tool) => new()
    {
        Name = tool.Name,
        Description = tool.Description,
        Schema = tool.JsonSchema.GetRawText(),
        IsDangerous = tool.ProtocolTool.Annotations?.DestructiveHint == true
    };
}
