using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Core.Exten;
using StackExchange.Redis;
using Xunit;

namespace OpenAgent.Core.Tests.Conversation;

/// <summary>
/// Verifies island-mode behaviour: with no Redis connection string configured,
/// AddAgentCore must not throw and must fall back to InMemory store/lock.
/// </summary>
public class IslandModeTests
{
    [Fact]
    public void AddAgentCore_NoRedisConfigured_DoesNotThrow_RegistersInMemoryFallback()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);

        // Should not throw even though Redis is unreachable/unconfigured.
        services.AddAgentCore(configuration);
        using var provider = services.BuildServiceProvider();

        // IConversationStore resolves to the InMemory fallback.
        var store = provider.GetRequiredService<IConversationStore>();
        Assert.NotNull(store);

        // IConversationLock resolves to the InMemory fallback.
        var locker = provider.GetRequiredService<IConversationLock>();
        Assert.NotNull(locker);

        // No IConnectionMultiplexer is registered when connection string is empty.
        Assert.Null(provider.GetService<IConnectionMultiplexer>());
    }
}
