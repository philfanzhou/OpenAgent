using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenAgent.Core.Abstract;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Engine.Abstractions;

namespace OpenAgent.Engine.Redis;

internal class RedisLlmRegistrar : RedisRegistrarBase<LlmProviderProfile>
{
    private static readonly JsonSerializerOptions LlmJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private readonly ILlmRegistry _llmRegistry;

    public RedisLlmRegistrar(
        IRedisConnectionProvider redis,
        ILlmRegistry llmRegistry,
        ILogger<RedisLlmRegistrar> logger)
        : base(redis, logger)
    {
        _llmRegistry = llmRegistry;
    }

    protected override string RegistrarName => "LLM";
    protected override string IndexKey => "llm:published:index";
    protected override string ItemKeyPrefix => "llm:registry";

    protected override LlmProviderProfile? Deserialize(string json) =>
        JsonSerializer.Deserialize<LlmProviderProfile>(json, LlmJsonOptions);

    protected override string? GetItemId(LlmProviderProfile item) => item.Id;

    protected override void Register(LlmProviderProfile item) => _llmRegistry.Register(item);
}
