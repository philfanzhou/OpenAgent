using OpenAgent.Router;
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
        RedisValue[] indexed = await redis.SetMembersAsync(EngineRegistryKeys.Index);
        string engineId = Assert.Single(indexed).ToString();
        RedisValue value = await redis.StringGetAsync($"engine:registry:{engineId}");
        using JsonDocument payload = JsonDocument.Parse(value.ToString());
        Assert.Equal("chat", payload.RootElement.GetProperty("Intents")[0].GetString());
    }

    [Fact]
    public async Task DeregisterAsync_RegisteredEngine_RemovesEntryAndIndexMember()
    {
        FakeRedisConnectionProvider redis = new();
        RedisRegistry registry = CreateRegistry(redis);
        await registry.RegisterAsync();

        await registry.DeregisterAsync();

        Assert.False(registry.IsRegistered);
        Assert.Empty(await redis.SetMembersAsync(EngineRegistryKeys.Index));
    }

    [Fact]
    public async Task RouterUrlConfigured_RegistersOverHttpWithoutTouchingRedis()
    {
        FakeRedisConnectionProvider redis = new();
        RecordingHandler handler = new();
        RedisRegistry registry = new(
            redis,
            new StaticOptionsMonitor(new HeartbeatOptions
            {
                AdvertisedHost = "engine",
                AdvertisedPort = 5208,
                RouterUrl = "http://router:8080",
                RegistrationKey = "s3cret"
            }),
            NullLogger<RedisRegistry>.Instance,
            handler);

        await registry.RegisterAsync();
        await registry.DeregisterAsync();

        Assert.Equal([HttpMethod.Put, HttpMethod.Delete], handler.Requests.Select(r => r.Method));
        Assert.All(handler.Requests, r =>
        {
            Assert.StartsWith("http://router:8080/api/v1/router/registrations/", r.Uri);
            Assert.Equal("s3cret", r.Key);
        });
        Assert.Contains("\"baseUrl\":\"http://engine:5208\"", handler.Requests[0].Body);
        Assert.Empty(await redis.SetMembersAsync(EngineRegistryKeys.Index));
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Uri, string? Key, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            request.Headers.TryGetValues("X-Registration-Key", out IEnumerable<string>? key);
            Requests.Add((request.Method, request.RequestUri!.ToString(), key?.SingleOrDefault(), body));
            return new HttpResponseMessage(System.Net.HttpStatusCode.NoContent);
        }
    }

    private static RedisRegistry CreateRegistry(FakeRedisConnectionProvider redis)
    {
        HeartbeatOptions options = new()
        {
            AdvertisedHost = "engine",
            AdvertisedPort = 5208,
            Intents = ["CHAT", "chat"]
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
