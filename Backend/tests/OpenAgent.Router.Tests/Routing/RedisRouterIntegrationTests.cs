using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Router.Options;
using OpenAgent.Router.Routing;
using StackExchange.Redis;
using Testcontainers.Redis;
using Xunit;

namespace OpenAgent.Router.Tests.Routing;

[CollectionDefinition(Name)]
public sealed class RedisRouterCollection : ICollectionFixture<RedisRouterFixture>
{
    public const string Name = "router-redis";
}

public sealed class RedisRouterFixture : IAsyncLifetime
{
    private RedisContainer? _container;

    public IConnectionMultiplexer Connection { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        if (!ContainerTestGuard.Enabled)
        {
            return;
        }

        _container = new RedisBuilder("redis:7-alpine").Build();
        await _container.StartAsync().ConfigureAwait(false);
        ConfigurationOptions options = ConfigurationOptions.Parse(_container.GetConnectionString());
        options.AllowAdmin = true;
        Connection = await ConnectionMultiplexer.ConnectAsync(options).ConfigureAwait(false);
    }

    public async Task DisposeAsync()
    {
        if (Connection != null)
        {
            await Connection.DisposeAsync().ConfigureAwait(false);
        }
        if (_container != null)
        {
            await _container.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task ResetAsync()
    {
        EndPoint endpoint = Connection.GetEndPoints().Single();
        await Connection.GetServer(endpoint).FlushDatabaseAsync().ConfigureAwait(false);
    }

    public string GetConnectionString() => _container?.GetConnectionString()
        ?? throw new InvalidOperationException("Container integration tests are disabled.");
}

[Collection(RedisRouterCollection.Name)]
[Trait("Category", "Container")]
public sealed class RedisRouterIntegrationTests(RedisRouterFixture fixture)
{
    [SkippableFact]
    public async Task RefreshAsync_ScaleAndExpiration_TracksIndexedHealthyEngines()
    {
        ContainerTestGuard.RequireEnabled();
        await fixture.ResetAsync();
        IDatabase database = fixture.Connection.GetDatabase();
        await WriteEngineAsync(database, Entry("engine-b", 2));
        await WriteEngineAsync(database, Entry("engine-a", 1));
        EngineRegistrySnapshotCache cache = CreateCache(fixture.Connection);

        await cache.RefreshAsync();

        Assert.Equal(["engine-a", "engine-b"], cache.Snapshot.Select(entry => entry.EngineId));

        await WriteEngineAsync(database, Entry("engine-c", 3));
        await database.KeyDeleteAsync("engine:registry:engine-b");
        await WriteEngineAsync(database, Entry(
            "engine-stale",
            0,
            heartbeat: DateTime.UtcNow.AddMinutes(-5)));
        await cache.RefreshAsync();

        Assert.Equal(["engine-a", "engine-c"], cache.Snapshot.Select(entry => entry.EngineId));
        RedisValue[] indexed = await database.SetMembersAsync(EngineRegistrySnapshotCache.RegistryIndexKey);
        Assert.Equal(
            ["engine-a", "engine-c"],
            indexed.Select(value => value.ToString()).Order(StringComparer.Ordinal));
    }

    [SkippableFact]
    public async Task GetTargetEndpoint_IntentLoadAndAffinity_SelectsEligibleEngine()
    {
        ContainerTestGuard.RequireEnabled();
        await fixture.ResetAsync();
        IDatabase database = fixture.Connection.GetDatabase();
        await WriteEngineAsync(database, Entry("engine-a", 10, ["chat"]));
        await WriteEngineAsync(database, Entry("engine-b", 1, ["chat"]));
        await WriteEngineAsync(database, Entry("engine-c", 0, ["workflow"]));
        EngineRegistrySnapshotCache cache = CreateCache(fixture.Connection);
        await cache.RefreshAsync();
        IEndpointHealthTracker tracker = new EndpointHealthTracker(
            1, TimeSpan.FromMinutes(1), TimeProvider.System);
        RedisServiceDiscoveryRouteTable routes = new(
            cache,
            NullLogger<RedisServiceDiscoveryRouteTable>.Instance,
            new JumpHashConsistentHashRing(),
            tracker);

        string? chatEndpoint = routes.GetTargetEndpoint("chat", null, null);
        string? workflowEndpoint = routes.GetTargetEndpoint("workflow", null, null);
        string? affinityFirst = routes.GetTargetEndpoint("chat", "tenant-1", "conversation-1");
        string? affinitySecond = routes.GetTargetEndpoint("chat", "tenant-1", "conversation-1");

        Assert.Equal("http://engine-b:5208", chatEndpoint);
        Assert.Equal("http://engine-c:5208", workflowEndpoint);
        Assert.Equal(affinityFirst, affinitySecond);
    }

    [SkippableFact]
    public async Task GetTargetEndpoint_QuarantinedEngines_FallsBackToHealthyThenStatic()
    {
        ContainerTestGuard.RequireEnabled();
        await fixture.ResetAsync();
        IDatabase database = fixture.Connection.GetDatabase();
        await WriteEngineAsync(database, Entry("engine-a", 0));
        await WriteEngineAsync(database, Entry("engine-b", 1));
        EngineRegistrySnapshotCache cache = CreateCache(fixture.Connection);
        await cache.RefreshAsync();
        EndpointHealthTracker tracker = new(1, TimeSpan.FromMinutes(1), TimeProvider.System);
        RedisServiceDiscoveryRouteTable dynamicRoutes = new(
            cache,
            NullLogger<RedisServiceDiscoveryRouteTable>.Instance,
            new JumpHashConsistentHashRing(),
            tracker);
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RouterSettings:Routing:EngineEndpoint"] = "http://static-engine:5208"
            })
            .Build();
        CompositeRouteTable routes = new(
            dynamicRoutes,
            new InMemoryRouteTable(configuration),
            NullLogger<CompositeRouteTable>.Instance);

        string first = Assert.IsType<string>(routes.GetTargetEndpoint("chat"));
        tracker.ReportFailure(first);
        string second = Assert.IsType<string>(routes.GetTargetEndpoint("chat"));
        tracker.ReportFailure(second);
        string fallback = Assert.IsType<string>(routes.GetTargetEndpoint("chat"));

        Assert.Equal("http://engine-a:5208", first);
        Assert.Equal("http://engine-b:5208", second);
        Assert.Equal("http://static-engine:5208", fallback);
    }

    [SkippableFact]
    public async Task RefreshAsync_RedisDisconnect_StaticOnlyClearsDynamicSnapshot()
    {
        ContainerTestGuard.RequireEnabled();
        await fixture.ResetAsync();
        IDatabase database = fixture.Connection.GetDatabase();
        await WriteEngineAsync(database, Entry("engine-a", 0));
        await using ConnectionMultiplexer connection = await ConnectionMultiplexer.ConnectAsync(
            fixture.GetConnectionString());
        EngineRegistrySnapshotCache cache = CreateCache(connection);
        await cache.RefreshAsync();
        Assert.Single(cache.Snapshot);

        await connection.CloseAsync();
        await cache.RefreshAsync();

        Assert.Empty(cache.Snapshot);
        Assert.False(cache.IsRedisAvailable);
    }

    [SkippableFact]
    public async Task RefreshAsync_RedisDisconnect_LastKnownExpiresAfterConfiguredAge()
    {
        ContainerTestGuard.RequireEnabled();
        await fixture.ResetAsync();
        IDatabase database = fixture.Connection.GetDatabase();
        await WriteEngineAsync(database, Entry("engine-a", 0));
        await using ConnectionMultiplexer connection = await ConnectionMultiplexer.ConnectAsync(
            fixture.GetConnectionString());
        MutableTimeProvider timeProvider = new(DateTimeOffset.UtcNow);
        EngineRegistrySnapshotCache cache = CreateCache(
            connection,
            RedisDiscoveryFailureMode.LastKnown,
            timeProvider);
        await cache.RefreshAsync();
        await connection.CloseAsync();

        await cache.RefreshAsync();
        Assert.Single(cache.Snapshot);

        timeProvider.Advance(TimeSpan.FromSeconds(16));
        await cache.RefreshAsync();
        Assert.Empty(cache.Snapshot);
    }

    [SkippableFact]
    public async Task AcquireAsync_ConcurrentRequests_EnforcesRedisBurstAtomically()
    {
        ContainerTestGuard.RequireEnabled();
        await fixture.ResetAsync();
        RateLimitSettings settings = new(0.001, 25, RateLimitFailureMode.FailClosed);
        RedisRateLimiter limiter = new(
            settings,
            NullLogger<RedisRateLimiter>.Instance,
            fixture.Connection,
            TimeProvider.System);

        RateLimitDecision[] decisions = await Task.WhenAll(
            Enumerable.Range(0, 100)
                .Select(_ => limiter.AcquireAsync("tenant:user")));

        Assert.Equal(25, decisions.Count(decision => decision.IsAllowed));
        Assert.All(
            decisions.Where(decision => !decision.IsAllowed),
            decision => Assert.True(decision.RetryAfter > TimeSpan.Zero));
        Assert.All(decisions, decision => Assert.Equal("redis", decision.Source));
    }

    [SkippableFact]
    public async Task RefreshAsync_PreCancelled_PropagatesCancellation()
    {
        ContainerTestGuard.RequireEnabled();
        await fixture.ResetAsync();
        EngineRegistrySnapshotCache cache = CreateCache(fixture.Connection);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => cache.RefreshAsync(cancellation.Token));
    }

    private static EngineRegistrySnapshotCache CreateCache(
        IConnectionMultiplexer connection,
        RedisDiscoveryFailureMode failureMode = RedisDiscoveryFailureMode.StaticOnly,
        TimeProvider? timeProvider = null) =>
        new(
            connection,
            NullLogger<EngineRegistrySnapshotCache>.Instance,
            new ServiceDiscoverySettings(
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(60),
                TimeSpan.FromSeconds(15),
                failureMode),
            timeProvider ?? TimeProvider.System);

    private static EngineRegistryEntry Entry(
        string engineId,
        int load,
        string[]? intents = null,
        DateTime? heartbeat = null) =>
        new()
        {
            EngineId = engineId,
            Host = engineId,
            Port = 5208,
            Load = load,
            LastHeartbeat = heartbeat ?? DateTime.UtcNow,
            Intents = intents ?? ["chat"]
        };

    private static async Task WriteEngineAsync(
        IDatabase database,
        EngineRegistryEntry entry)
    {
        await database.StringSetAsync(
            $"engine:registry:{entry.EngineId}",
            JsonSerializer.Serialize(entry),
            TimeSpan.FromMinutes(1));
        await database.SetAddAsync(
            EngineRegistrySnapshotCache.RegistryIndexKey,
            entry.EngineId);
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;

        internal void Advance(TimeSpan duration)
        {
            utcNow += duration;
        }
    }
}
