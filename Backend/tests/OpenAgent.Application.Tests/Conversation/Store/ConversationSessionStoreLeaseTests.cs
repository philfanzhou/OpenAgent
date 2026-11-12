using Microsoft.Extensions.Options;
using Moq;
using OpenAgent.Application.Conversation;
using OpenAgent.Application.Conversation.Lock;
using OpenAgent.Application.Conversation.Store;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using Xunit;

namespace OpenAgent.Application.Tests.Conversation;

public sealed class ConversationSessionStoreLeaseTests
{
    [Theory]
    [InlineData("read")]
    [InlineData("retry")]
    [InlineData("status")]
    public async Task SaveAsync_LockLostDuringStorageAwait_DoesNotStartNextMutation(string lossPoint)
    {
        ICurrentUserContext user = Mock.Of<ICurrentUserContext>(value =>
            value.UserId == "user-1" && value.TenantId == "tenant-1" && value.IsAuthenticated);
        InMemoryConversationStore durable = new(user);
        ConversationRecord record = new()
        {
            TenantId = "tenant-1", UserId = "user-1", ConversationId = "conversation-1",
            Version = lossPoint == "retry" ? 8 : 7,
            MessageCount = lossPoint == "retry" ? 1 : 0,
            Messages = lossPoint == "retry" ? [Message("prior", "prior message")] : []
        };
        Assert.True(await durable.CreateAsync(record));
        InMemoryConversationLock conversationLock = new();
        await using IConversationLockHandle? handle = await conversationLock.TryAcquireAsync(
            "tenant-1", "conversation-1", TimeSpan.FromMinutes(1));
        Assert.NotNull(handle);
        TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int reads = 0;
        Mock<IConversationStore> boundary = new();
        boundary.Setup(value => value.GetRecordAsync("tenant-1", "conversation-1", It.IsAny<CancellationToken>()))
            .Returns(async (string tenant, string conversation, CancellationToken token) =>
            {
                reads++;
                if ((lossPoint == "read" && reads == 1) || (lossPoint == "retry" && reads == 2))
                {
                    reached.TrySetResult();
                    await resume.Task;
                }
                return await durable.GetRecordAsync(tenant, conversation, token);
            });
        boundary.Setup(value => value.AppendMessagesAsync("tenant-1", "conversation-1", It.IsAny<int>(),
                It.IsAny<IReadOnlyList<ConversationMessage>>(), It.IsAny<CancellationToken>()))
            .Returns(async (string tenant, string conversation, int version, IReadOnlyList<ConversationMessage> messages, CancellationToken token) =>
            {
                AppendResult result = await durable.AppendMessagesAsync(tenant, conversation, version, messages, token);
                if (lossPoint == "status")
                {
                    reached.TrySetResult();
                    await resume.Task;
                }
                return result;
            });
        boundary.Setup(value => value.UpdateStatusAsync("tenant-1", "conversation-1", It.IsAny<ConversationStatus>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((string tenant, string conversation, ConversationStatus status, int version, CancellationToken token) =>
                durable.UpdateStatusAsync(tenant, conversation, status, version, token));
        ConversationSessionStore sessions = new(boundary.Object, Options.Create(new ConversationStoreOptions()));
        ConversationContext context = new("conversation-1", "tenant-1", "user-1", "agent-1", "trace", ConversationType.User);

        Task<int?> saving = sessions.SaveAsync(context, handle, 7,
            [Message("owned-append", "owned append")], ConversationStatus.Completed, CancellationToken.None);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await handle.DisposeAsync();
        resume.SetResult();
        AgentException error = await Assert.ThrowsAsync<AgentException>(() => saving);

        Assert.Equal(AgentErrorCode.Conflict, error.ErrorCode);
        Assert.Equal(ConversationStatus.Running, record.Status);
        Assert.Equal(lossPoint == "read" ? 7 : 8, record.Version);
        Assert.Equal(lossPoint == "read" ? 0 : 1, record.MessageCount);
        Assert.Equal(lossPoint switch
        {
            "retry" => new[] { "prior message" },
            "status" => new[] { "owned append" },
            _ => Array.Empty<string>()
        }, record.Messages.Select(message => message.Content));
    }

    private static ConversationMessage Message(string id, string content) => new()
    {
        MessageId = id, Sequence = 1, Role = "user", Content = content
    };
}
