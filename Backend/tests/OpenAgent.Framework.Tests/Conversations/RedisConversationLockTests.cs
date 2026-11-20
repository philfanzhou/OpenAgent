using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OpenAgent.Contracts.Conversation;
using StackExchange.Redis;
using Xunit;

namespace OpenAgent.Framework.Tests.Conversations;

public sealed class RedisConversationLockTests
{
    [Fact]
    public async Task IsHeld_RenewalRejectedByOtherOwner_BecomesFalseBeforeLeaseExpires()
    {
        Mock<IDatabase> database = CreateDatabase();
        TaskCompletionSource renewal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        database.Setup(value => value.ScriptEvaluateAsync(It.IsAny<string>(), It.IsAny<RedisKey[]>(),
                It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()))
            .Returns((string script, RedisKey[] keys, RedisValue[] values, CommandFlags flags) =>
            {
                if (values.Length == 1)
                {
                    return Task.FromResult(RedisResult.Create((RedisValue)1L));
                }
                renewal.TrySetResult();
                return Task.FromResult(RedisResult.Create((RedisValue)0L));
            });
        RedisConversationLock conversationLock = CreateLock(database.Object);
        await using IConversationLockHandle? handle = await conversationLock.TryAcquireAsync("tenant-1", "conversation-1", TimeSpan.FromSeconds(6));
        Assert.NotNull(handle);
        Assert.True(handle.IsHeld);

        await renewal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(1));
        while (handle.IsHeld)
        {
            await Task.Delay(10, timeout.Token);
        }

        Assert.False(handle.IsHeld);
    }

    [Theory]
    [InlineData("unavailable")]
    [InlineData("cancelled")]
    public async Task IsHeld_SingleTransientRenewalFailure_ToleratedAndLockStaysHeld(string failure)
    {
        Mock<IDatabase> database = CreateDatabase();
        TaskCompletionSource renewal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        database.Setup(value => value.ScriptEvaluateAsync(It.IsAny<string>(), It.IsAny<RedisKey[]>(),
                It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()))
            .Returns((string script, RedisKey[] keys, RedisValue[] values, CommandFlags flags) =>
            {
                if (values.Length == 1)
                {
                    return Task.FromResult(RedisResult.Create((RedisValue)1L));
                }
                renewal.TrySetResult();
                return failure switch
                {
                    "cancelled" => Task.FromException<RedisResult>(new OperationCanceledException("Renewal cancelled")),
                    _ => Task.FromException<RedisResult>(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Unavailable")),
                };
            });
        RedisConversationLock conversationLock = CreateLock(database.Object);
        // TTL 3.3s keeps the heartbeat interval at its 1s floor: one failure lands ~1s in.
        await using IConversationLockHandle? handle = await conversationLock.TryAcquireAsync("tenant-1", "conversation-1", TimeSpan.FromMilliseconds(3300));
        Assert.NotNull(handle);
        Assert.True(handle.IsHeld);

        await renewal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // One transient failure must NOT give up the lock: it is still held shortly after.
        await Task.Delay(300);
        Assert.True(handle.IsHeld);
    }

    [Fact]
    public async Task IsHeld_AcquisitionResponseArrivesAfterExpiry_DoesNotClaimOwnership()
    {
        Mock<IDatabase> database = CreateDatabase();
        TaskCompletionSource<bool> acquired = new(TaskCreationOptions.RunContinuationsAsynchronously);
        database.Setup(value => value.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(),
                It.IsAny<TimeSpan?>(), It.IsAny<When>()))
            .Returns(acquired.Task);
        database.Setup(value => value.ScriptEvaluateAsync(It.IsAny<string>(), It.IsAny<RedisKey[]>(),
                It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(RedisResult.Create((RedisValue)0L));
        RedisConversationLock conversationLock = CreateLock(database.Object);
        Task<IConversationLockHandle?> acquiring = conversationLock.TryAcquireAsync("tenant-1", "conversation-1", TimeSpan.FromMilliseconds(30));
        await Task.Delay(60);
        acquired.SetResult(true);

        await using IConversationLockHandle? handle = await acquiring;

        Assert.NotNull(handle);
        Assert.False(handle.IsHeld);
    }

    private static Mock<IDatabase> CreateDatabase()
    {
        Mock<IDatabase> database = new();
        database.Setup(value => value.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(),
                It.IsAny<TimeSpan?>(), It.IsAny<When>()))
            .ReturnsAsync(true);
        return database;
    }

    private static RedisConversationLock CreateLock(IDatabase database)
    {
        Mock<IConnectionMultiplexer> connection = new();
        connection.Setup(value => value.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(database);
        return new RedisConversationLock(connection.Object, NullLogger<RedisConversationLock>.Instance);
    }
}
