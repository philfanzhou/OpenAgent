using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenAgent.Engine.Models;
using OpenAgent.Engine.Registry;
using StackExchange.Redis;
using Xunit;

namespace OpenAgent.Engine.Tests.Registry;

public class RedisRegistryTests
{
    [Fact]
    public async Task RegisterAsync_Success_StoresEntryAndAddsIndexMember()
    {
        FakeRedisConnectionProvider redis = new();
        RedisRegistry registry = CreateRegistry(redis);

        await registry.RegisterAsync();

        Assert.True(registry.IsRegistered);
        RedisValue[] indexed = await redis.SetMembersAsync(RedisRegistry.RegistryIndexKey);
        string engineId = Assert.Single(indexed).ToString();
        RedisValue value = await redis.StringGetAsync($"engine:registry:{engineId}");
        using JsonDocument payload = JsonDocument.Parse(value.ToString());
        Assert.Equal("chat", payload.RootElement.GetProperty("Intents")[0].GetString());
        Assert.Equal("mcp", payload.RootElement.GetProperty("Capabilities")[0].GetString());
    }

    [Fact]
    public async Task DeregisterAsync_RegisteredEngine_RemovesEntryAndIndexMember()
    {
        FakeRedisConnectionProvider redis = new();
        RedisRegistry registry = CreateRegistry(redis);
        await registry.RegisterAsync();

        await registry.DeregisterAsync();

        Assert.False(registry.IsRegistered);
        Assert.Empty(await redis.SetMembersAsync(RedisRegistry.RegistryIndexKey));
    }

    private static RedisRegistry CreateRegistry(FakeRedisConnectionProvider redis)
    {
        HeartbeatOptions options = new()
        {
            AdvertisedHost = "engine",
            AdvertisedPort = 5208,
            Intents = ["CHAT", "chat"],
            Capabilities = ["MCP"]
        };
        return new RedisRegistry(
            redis,
            new StaticOptionsMonitor(options),
            NullLogger<RedisRegistry>.Instance);
    }

    private sealed class StaticOptionsMonitor(HeartbeatOptions value) : IOptionsMonitor<HeartbeatOptions>
    {
        public HeartbeatOptions CurrentValue => value;
        public HeartbeatOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<HeartbeatOptions, string?> listener) => null;
    }
}
