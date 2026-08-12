using OpenAgent.Contracts.Configuration;
using OpenAgent.Core.Capabilities.Mcp;
using Xunit;

namespace OpenAgent.Core.Tests.Capabilities;

public class McpServerClientTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("2024-11-05", "2024-11-05")]
    [InlineData("2025-03-26", "2025-03-26")]
    [InlineData("2025-06-18", "2025-06-18")]
    [InlineData("2025-11-25", "2025-11-25")]
    public void CreateClientOptions_ConfiguredProtocolVersion_PinsRequestedVersion(
        string? configured,
        string? expected)
    {
        var server = new McpServerConfig { ProtocolVersion = configured };

        ModelContextProtocol.Client.McpClientOptions options = McpServerClient.CreateClientOptions(server);

        Assert.Equal(expected, options.ProtocolVersion);
    }

    [Fact]
    public void McpServerIdentity_DifferentProtocolVersions_AreDifferentConnections()
    {
        McpServerIdentity first = McpServerIdentity.From(new McpServerConfig
        {
            Name = "tools",
            Url = "https://mcp.example.test",
            ProtocolVersion = "2024-11-05"
        });
        McpServerIdentity second = McpServerIdentity.From(new McpServerConfig
        {
            Name = "tools",
            Url = "https://mcp.example.test",
            ProtocolVersion = "2025-06-18"
        });

        Assert.NotEqual(first, second);
    }
}
