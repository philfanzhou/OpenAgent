using System.Text.Json;
using OpenAgent.Contracts.Configuration;
using Xunit;

namespace OpenAgent.Core.Tests.Mcp;

public sealed class McpConfigurationTests
{
    [Fact]
    public void McpServerConfig_WhenTypeIsOmitted_DefaultsToHttp()
    {
        var server = new McpServerConfig();

        Assert.Equal(McpServerType.Http, server.Type);
    }

    [Fact]
    public void AgentConfig_LegacyStringMcpServer_DefaultsToHttp()
    {
        const string json = """
            {
              "Mcp": {
                "Servers": ["https://mcp.example.test/custom/path"]
              }
            }
            """;

        AgentConfig? config = JsonSerializer.Deserialize<AgentConfig>(json);

        McpServerConfig server = Assert.Single(Assert.IsType<AgentConfig>(config).Mcp.Servers);
        Assert.Equal(McpServerType.Http, server.Type);
    }
}
