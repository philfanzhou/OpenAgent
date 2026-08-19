using ModelContextProtocol.Client;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Core.Capabilities.Mcp;
using Xunit;

namespace OpenAgent.Core.Tests.Capabilities;

public sealed class McpToolFactoryTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("2025-06-18", "2025-06-18")]
    [InlineData("2026-07-28", "2026-07-28")]
    public void CreateClientOptions_UsesOfficialMcpClientOptions(
        string? configured,
        string? expected)
    {
        McpClientOptions options = McpToolFactory.CreateClientOptions(
            new McpServerConfig { ProtocolVersion = configured });

        Assert.Equal(expected, options.ProtocolVersion);
        Assert.Equal("OpenAgent", options.ClientInfo?.Name);
    }
}
