using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAgent.Contracts.Conversation;

namespace OpenAgent.Contracts.Configuration;

/// <summary>
/// Represents the structured configuration data for the Agent runtime.
/// This model is decoupled from configuration sources (appsettings, db, etc.).
/// </summary>
public class AgentConfig
{
    public string TenantId { get; set; } = string.Empty;
    public string Instructions { get; set; } = string.Empty;
    public LlmConfig Llm { get; set; } = new();
    public McpConfig Mcp { get; set; } = new();
    public RagConfig Rag { get; set; } = new();
    public SkillsConfig Skills { get; set; } = new();
    public ContextPolicy? ContextPolicy { get; set; }

    public int MaxTurns { get; set; } = 50;
}

public class LlmConfig
{
    public string TenantId { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public ApiFormat Format { get; set; } = ApiFormat.OpenAIChatCompletions;
    public string ModelId { get; set; } = "gpt-4o";
    public string ApiKey { get; set; } = string.Empty;
    public string Endpoint { get; set; } = string.Empty;
    public double Temperature { get; set; } = 0.7;
}

public class LlmProviderProfile
{
    public string TenantId { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public ApiFormat Format { get; set; } = ApiFormat.OpenAIChatCompletions;
    // Kept for deserializing old provider profiles; new profiles select a model per Agent.
    public string? ModelId { get; set; }
    public string Endpoint { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public double Temperature { get; set; } = 0.7;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ApiFormat
{
    OpenAIChatCompletions,
    OpenAIResponses,
    AnthropicMessages
}

public class McpConfig
{
    public List<string> EnabledServerIds { get; set; } = new();

    [JsonConverter(typeof(McpServersConverter))]
    // Compatibility payload for configurations written before MCP profiles
    // became independent resources. New Agent configs should save IDs only.
    public List<McpServerConfig> Servers { get; set; } = new();
}

public class McpServerConfig
{
    public string TenantId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public McpServerType Type { get; set; } = McpServerType.Http;
    public string? ProtocolVersion { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum McpServerType
{
    SSE,
    Http
}

internal class McpServersConverter : JsonConverter<List<McpServerConfig>>
{
    public override List<McpServerConfig>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.StartArray)
        {
            var result = new List<McpServerConfig>();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray) break;

                if (reader.TokenType == JsonTokenType.String)
                {
                    var url = reader.GetString();
                    if (!string.IsNullOrEmpty(url))
                    {
                        result.Add(new McpServerConfig
                        {
                            Name = url.Replace("http://", "").Replace("https://", "").Split('/')[0],
                            Url = url,
                            Type = McpServerType.Http
                        });
                    }
                }
                else if (reader.TokenType == JsonTokenType.StartObject)
                {
                    var config = JsonSerializer.Deserialize<McpServerConfig>(ref reader, options);
                    if (config != null) result.Add(config);
                }
            }
            return result;
        }

        return new List<McpServerConfig>();
    }

    public override void Write(Utf8JsonWriter writer, List<McpServerConfig> value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, value, options);
    }
}

public class RagInstanceConfig
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public string Type { get; set; } = RagAdapterType.RagFlow;
    public string CollectionName { get; set; } = "default";
    public string ApiEndpoint { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public Dictionary<string, string>? AdapterConfig { get; set; }

    public List<string> AllowedUserIds { get; set; } = new();
    public List<string> AllowedGroups { get; set; } = new();
    public List<string> AllowedTenantIds { get; set; } = new();
    public List<string> AllowedRoles { get; set; } = new();
}

public static class RagAdapterType
{
    public const string RagFlow = "ragflow";
    public const string Qdrant = "qdrant";
}

public class RagConfig
{
    public bool Enabled { get; set; } = false;
    public List<string> EnabledRagInstanceIds { get; set; } = new();
    public List<RagInstanceConfig> Instances { get; set; } = new();
}

public class SkillInstanceConfig
{
    public string TenantId { get; set; } = string.Empty;
    [JsonPropertyName("skillId")]
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public string Description { get; set; } = string.Empty;
    public string ParametersJsonSchema { get; set; } = string.Empty;
    public string Type { get; set; } = SkillTypes.AgentSkill;
    public string Source { get; set; } = "Local";
    public string SourceType { get; set; } = SkillSourceTypes.ObjectStorage;
    public string? SourceId { get; set; }
    public string? PackageFileName { get; set; }
    public string? PackageFormat { get; set; }
    public string? ObjectKey { get; set; }
    public string? Sha256 { get; set; }
    public int ResourceCount { get; set; }

    public List<string> AllowedUserIds { get; set; } = new();
    public List<string> AllowedGroups { get; set; } = new();
    public List<string> AllowedTenantIds { get; set; } = new();
    public List<string> AllowedRoles { get; set; } = new();
}

public static class SkillTypes
{
    public const string AgentSkill = "AgentSkill";
}

public static class SkillSourceTypes
{
    public const string ObjectStorage = "ObjectStorage";
    public const string PostgreSql = "PostgreSql";
    public const string Redis = "Redis";
}

public class SkillsConfig
{
    public List<string> EnabledSkills { get; set; } = new();
    public List<SkillInstanceConfig> Instances { get; set; } = new();
}
