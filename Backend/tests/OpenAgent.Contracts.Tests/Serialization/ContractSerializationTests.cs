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

    [Fact]
    public void TokenUsage_LegacyPayload_RemainsCompatible()
    {
        const string json = """
            {"promptTokens":12,"completionTokens":4,"totalTokens":16}
            """;

        TokenUsage? usage = JsonSerializer.Deserialize<TokenUsage>(json, JsonOptions);

        TokenUsage actual = Assert.IsType<TokenUsage>(usage);
        Assert.Equal(12, actual.PromptTokens);
        Assert.Equal(4, actual.CompletionTokens);
        Assert.Equal(16, actual.TotalTokens);
        Assert.Null(actual.CachedInputTokens);
        Assert.Null(actual.ReasoningTokens);
    }

    [Fact]
    public void ChatResponse_LegacyPayload_LeavesUsageUnavailable()
    {
        const string json = """{"message":"hello"}""";

        ChatResponse? response = JsonSerializer.Deserialize<ChatResponse>(json, JsonOptions);

        ChatResponse actual = Assert.IsType<ChatResponse>(response);
        Assert.Equal("hello", actual.Message);
        Assert.Null(actual.Usage);
        Assert.Null(actual.ModelId);
    }
}
