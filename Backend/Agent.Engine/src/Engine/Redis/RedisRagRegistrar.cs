using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenAgent.Core.Abstract;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Engine.Abstractions;

namespace OpenAgent.Engine.Redis;

internal class RedisRagRegistrar : RedisRegistrarBase<RagInstanceConfig>
{
    private static readonly JsonSerializerOptions RagJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IRagRegistry _ragRegistry;

    public RedisRagRegistrar(
        IRedisConnectionProvider redis,
        IRagRegistry ragRegistry,
        ILogger<RedisRagRegistrar> logger)
        : base(redis, logger)
    {
        _ragRegistry = ragRegistry;
    }

    protected override string RegistrarName => "RAG";
    protected override string IndexKey => "rag:published:index";
    protected override string ItemKeyPrefix => "rag:registry";

    protected override RagInstanceConfig? Deserialize(string json) =>
        JsonSerializer.Deserialize<RagInstanceConfig>(json, RagJsonOptions);

    protected override string? GetItemId(RagInstanceConfig item) => item.Id;

    protected override void Register(RagInstanceConfig item) => _ragRegistry.Register(item);
}
