using ModelContextProtocol.Client;
using OpenAgent.Contracts.Configuration;

namespace OpenAgent.Application.Capabilities.Mcp;

/// <summary>管理与测试连接共用的 MCP 客户端握手选项。</summary>
internal static class McpClientOptionsFactory
{
    internal static McpClientOptions Create(McpServerConfig server) => new()
    {
        ClientInfo = new ModelContextProtocol.Protocol.Implementation
        {
            Name = "OpenAgent",
            Version = "1.0.0"
        },
        ProtocolVersion = string.IsNullOrWhiteSpace(server.ProtocolVersion)
            ? null
            : server.ProtocolVersion.Trim(),
        InitializationTimeout = TimeSpan.FromSeconds(30)
    };
}
