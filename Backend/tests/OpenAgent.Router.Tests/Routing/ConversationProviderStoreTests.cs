using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using OpenAgent.Router.Models;
using OpenAgent.Router.Routing;
using StackExchange.Redis;
using Xunit;

namespace OpenAgent.Router.Tests.Routing;

public class ConversationProviderStoreTests
{
    [Fact]
    public async Task BindAsync_ConcurrentProviderClaims_FirstProviderWinsWithinTenant()
    {
        var services = new ServiceCollection();
        services.AddDistributedMemoryCache();
        using ServiceProvider serviceProvider = services.BuildServiceProvider();
        var store = new ConversationProviderStore(
            serviceProvider.GetRequiredService<IDistributedCache>());
        var engine = new ConversationProviderAffinity(
            "self-engine",
            ConversationAffinityState.Pending);
        var partner = new ConversationProviderAffinity(
            "partner",
            ConversationAffinityState.Pending);

        ConversationProviderAffinity first = await store.BindAsync(
            "tenant-1",
            "conversation-1",
            engine,
            CancellationToken.None);
        ConversationProviderAffinity second = await store.BindAsync(
            "tenant-1",
            "conversation-1",
            partner,
            CancellationToken.None);
        ConversationProviderAffinity otherTenant = await store.BindAsync(
            "tenant-2",
            "conversation-1",
            partner,
            CancellationToken.None);

        Assert.Equal("self-engine", first.ProviderId);
        Assert.Equal("self-engine", second.ProviderId);
        Assert.Equal("partner", otherTenant.ProviderId);
    }

    [Fact]
    public async Task AffinityKeys_AppliedWithExpiration()
    {
        var database = new Mock<IDatabase>();
        var redis = new Mock<IConnectionMultiplexer>();
        redis.Setup(value => value.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(database.Object);
        var store = new ConversationProviderStore(
            new Mock<IDistributedCache>().Object, redis.Object);
        var affinity = new ConversationProviderAffinity(
            "self-engine",
            ConversationAffinityState.Pending);

        await store.SetAsync("tenant-1", "conversation-1", affinity, CancellationToken.None);
        await store.BindAsync("tenant-1", "conversation-2", affinity, CancellationToken.None);

        // Affinity keys must carry an expiry on every write (any overload),
        // otherwise they grow without bound as conversations accumulate.
        var stringSets = database.Invocations
            .Where(invocation => invocation.Method.Name == nameof(IDatabase.StringSetAsync))
            .ToList();
        Assert.Equal(2, stringSets.Count);
        Assert.All(stringSets, invocation => Assert.Contains(
            invocation.Arguments, argument => argument is TimeSpan ttl && ttl > TimeSpan.Zero));
    }
}