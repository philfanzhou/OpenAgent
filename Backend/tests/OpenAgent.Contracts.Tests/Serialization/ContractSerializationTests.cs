using System.Text.Json;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Content;
using OpenAgent.Contracts.Requests;
using Xunit;

namespace OpenAgent.Contracts.Tests.Serialization;

public class ContractSerializationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void AgentConfig_LegacyMcpUrlList_MapsToHttpServers()
    {
        const string json = """
            {"mcp":{"servers":["https://mcp.example/tools"]}}
            """;

        AgentConfig? config = JsonSerializer.Deserialize<AgentConfig>(json, JsonOptions);

        McpServerConfig server = Assert.Single(Assert.IsType<AgentConfig>(config).Mcp.Servers);
        Assert.Equal("mcp.example", server.Name);
        Assert.Equal("https://mcp.example/tools", server.Url);
        Assert.Equal(McpServerType.Http, server.Type);
    }

    [Fact]
    public void AgentConfig_StructuredStdioServer_RoundTrips()
    {
        AgentConfig config = new()
        {
            Mcp = new McpConfig
            {
                Servers =
                [
                    new McpServerConfig
                    {
                        Name = "local-tools",
                        Type = McpServerType.Stdio,
                        Command = "dotnet",
                        Arguments = ["run", "--project", "Tools"]
                    }
                ]
            }
        };

        string json = JsonSerializer.Serialize(config, JsonOptions);
        AgentConfig? roundTrip = JsonSerializer.Deserialize<AgentConfig>(json, JsonOptions);

        McpServerConfig server = Assert.Single(Assert.IsType<AgentConfig>(roundTrip).Mcp.Servers);
        Assert.Equal(McpServerType.Stdio, server.Type);
        Assert.Equal("dotnet", server.Command);
        Assert.Equal(["run", "--project", "Tools"], server.Arguments);
    }

    [Fact]
    public void AgentRequest_Attachments_AreNotSerialized()
    {
        AgentRequest request = new()
        {
            Query = "hello",
            Attachments =
            [
                new AgentAttachment
                {
                    FileName = "sample.txt",
                    MediaType = "text/plain",
                    Data = [1, 2, 3, 4],
                    ObjectKey = "private/attachment.txt",
                    Sha256 = "sha-256"
                }
            ]
        };

        string json = JsonSerializer.Serialize(request, JsonOptions);

        Assert.DoesNotContain("attachments", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private/attachment.txt", json, StringComparison.Ordinal);
        Assert.Contains("hello", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ApiFormat_SerializesAsStableString()
    {
        string json = JsonSerializer.Serialize(ApiFormat.OpenAIResponses, JsonOptions);

        Assert.Equal("\"OpenAIResponses\"", json);
    }

    [Fact]
    public void SkillInstanceConfig_UsesSkillIdWireName()
    {
        SkillInstanceConfig skill = new() { Id = "search", Name = "Search" };

        string json = JsonSerializer.Serialize(skill, JsonOptions);

        Assert.Contains("\"skillId\":\"search\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"id\"", json, StringComparison.OrdinalIgnoreCase);
    }
}
