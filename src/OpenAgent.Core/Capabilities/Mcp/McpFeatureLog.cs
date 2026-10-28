using Microsoft.Extensions.Logging;

namespace OpenAgent.Core.Capabilities.Mcp;

internal static partial class McpFeatureLog
{
    [LoggerMessage(EventId = 2200, Level = LogLevel.Information,
        Message = "MCP tools deferred: {Deferred} tools hidden behind search_tools (threshold {Threshold})")]
    internal static partial void ToolsDeferred(ILogger logger, int deferred, int threshold);
}
