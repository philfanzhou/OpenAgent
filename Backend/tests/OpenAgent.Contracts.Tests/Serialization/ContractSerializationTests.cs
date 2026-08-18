using System.Text.Json;
using OpenAgent.Contracts.Configuration;
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
    public void AgentRequest_FileIds_AreNotSerialized()
    {
        AgentRequest request = new()
        {
            Query = "hello",
            FileIds = ["file-001"]
        };

        string json = JsonSerializer.Serialize(request, JsonOptions);

        Assert.DoesNotContain("fileIds", json, StringComparison.OrdinalIgnoreCase);
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
