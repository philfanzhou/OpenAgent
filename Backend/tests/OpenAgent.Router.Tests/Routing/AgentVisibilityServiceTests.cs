using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OpenAgent.Contracts.Security;
using OpenAgent.Router.Models;
using OpenAgent.Router.Security;
using StackExchange.Redis;
using Xunit;

namespace OpenAgent.Router.Tests.Routing;

public sealed class AgentVisibilityServiceTests
{
    [Fact]
    public async Task Acl_UsesValidSnapshotDuringOutageAndFailsClosedWhenExpired()
    {
        Mock<IConnectionMultiplexer> redis = new();
        Mock<IDatabase> database = new();
        redis.Setup(value => value.GetDatabase(-1, null)).Returns(database.Object);
        const string key = "agent:acl:agent";
        var read = database.Setup(value => value.StringGetAsync(key, CommandFlags.None));
        read.ReturnsAsync("{\"AllowedUserIds\":[\"owner\"]}");
        TestClock clock = new();
        AgentVisibilityService service = new(NullLogger<AgentVisibilityService>.Instance, redis.Object, clock);
        AgentUserContext owner = new() { UserId = "owner" };
        AgentUserContext other = new() { UserId = "other" };

        Assert.True(await service.IsAgentVisibleToUserAsync("agent", owner));
        Assert.False(await service.IsAgentVisibleToUserAsync("agent", other));
        read.ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "offline"));
        Assert.True(await service.IsAgentVisibleToUserAsync("agent", owner));
        Assert.False(await service.IsAgentVisibleToUserAsync("agent", other));
        clock.Timestamp += 31;
        AgentRoutingException expired = await Assert.ThrowsAsync<AgentRoutingException>(() => service.IsAgentVisibleToUserAsync("agent", owner));
        Assert.Equal(503, expired.StatusCode);
        Assert.Equal("agent-authorization-unavailable", expired.Code);

        read.ReturnsAsync("{\"AllowedUserIds\":[\"other\"]}");
        Assert.False(await service.IsAgentVisibleToUserAsync("agent", owner));
        Assert.True(await service.IsAgentVisibleToUserAsync("agent", other));
        clock.Timestamp += 31;
        read.ReturnsAsync(RedisValue.Null);
        Assert.True(await service.IsAgentVisibleToUserAsync("agent", owner));
    }

    private sealed class TestClock : TimeProvider
    {
        internal long Timestamp { get; set; }
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => Timestamp;
    }
}
