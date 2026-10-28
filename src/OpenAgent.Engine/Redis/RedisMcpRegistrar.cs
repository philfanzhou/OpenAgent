using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Core.Abstract;
using OpenAgent.Engine.Abstractions;

namespace OpenAgent.Engine.Redis;

internal sealed class RedisMcpRegistrar : RedisRegistrarBase<McpServerConfig>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IMcpRegistry _registry;

    public RedisMcpRegistrar(
        IRedisConnectionProvider redis,
        IMcpRegistry registry,
        ILogger<RedisMcpRegistrar> logger)
        : base(redis, logger)
    {
        _registry = registry;
    }

    protected override string RegistrarName => "MCP catalog";
    protected override string IndexKey => "mcp:published:index";
    protected override string ItemKeyPrefix => "mcp:registry";
    protected override McpServerConfig? Deserialize(string json) =>
        JsonSerializer.Deserialize<McpServerConfig>(json, JsonOptions);
    protected override string? GetItemId(McpServerConfig item) => item.Name;
    protected override void Register(McpServerConfig item) => _registry.Register(item);
}
