using Microsoft.Extensions.Logging;

namespace OpenAgent.Core.Capabilities.Mcp;

internal static partial class McpLog
{
    [LoggerMessage(EventId = 1234, Level = LogLevel.Error, Message = "Failed to connect to MCP server")]
    public static partial void ConnectFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1263, Level = LogLevel.Error, Message = "Failed to read MCP resource: {Uri}")]
    public static partial void ReadMcpResourceFailed(ILogger logger, Exception exception, string uri);

    [LoggerMessage(EventId = 1264, Level = LogLevel.Warning, Message = "MCP discovery failed. Server={Server}")]
    public static partial void DiscoveryFailed(ILogger logger, Exception exception, string server);
}
