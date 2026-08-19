using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Conversation;
using OpenAgent.Core.Conversation.Store;
using Xunit;

namespace OpenAgent.Core.Tests.Conversation;

public class InMemoryConversationStoreTests
{
    private static ICurrentUserContext UserContext(string userId = "u1") => new FakeUserContext(userId);

    private sealed class FakeUserContext(string userId) : ICurrentUserContext
    {
        public string UserId => userId;
        public string? TenantId => null;
        public bool IsAuthenticated => true;
        public IReadOnlyList<string> Roles => [];
        public bool IsInRole(string role) => false;
    }

    private static ConversationRecord CreateRecord(
        string tenantId = "t1",
        string conversationId = "c1",
        string userId = "u1",
        ConversationType type = ConversationType.User,
        ConversationOwnerRole ownerRole = ConversationOwnerRole.User)
    {
        return new ConversationRecord
        {
            TenantId = tenantId,
            ConversationId = conversationId,
            UserId = userId,
            Type = type,
            OwnerRole = ownerRole,
            AgentId = "a1"
        };
    }

    private static ConversationMessage CreateMessage(
        string messageId,
        int sequence,
        string role = "user",
        string content = "hello",
        string? idempotencyKey = null)
    {
        return new ConversationMessage
        {
            MessageId = messageId,
            Sequence = sequence,
            Role = role,
            Content = content,
            IdempotencyKey = idempotencyKey
        };
    }

    [Fact]
    public async Task CreateAsync_NewRecord_ReturnsTrue()
    {
        var store = new InMemoryConversationStore(UserContext());
        var result = await store.CreateAsync(CreateRecord());
        Assert.True(result);
    }

    [Fact]
    public async Task CreateAsync_Duplicate_ReturnsFalse()
    {
        var store = new InMemoryConversationStore(UserContext());
        await store.CreateAsync(CreateRecord());
        var second = await store.CreateAsync(CreateRecord());
        Assert.False(second);
    }

    [Fact]
    public async Task GetRecordAsync_Missing_ReturnsNull()
    {
        var store = new InMemoryConversationStore(UserContext());
        var result = await store.GetRecordAsync("t1", "missing");
        Assert.Null(result);
    }

    [Fact]
    public async Task AppendMessages_NewMessages_IncrementsVersionAndCount()
    {
        var store = new InMemoryConversationStore(UserContext());
        await store.CreateAsync(CreateRecord());

        var result = await store.AppendMessagesAsync(
            "t1", "c1", 1, new[] { CreateMessage("m1", 1) });

        Assert.True(result.Success);
        Assert.Equal(2, result.NewVersion);
        Assert.Equal(1, result.NewMessageCount);
    }

    [Fact]
    public async Task AppendMessages_VersionConflict_ReturnsConflict()
    {
        var store = new InMemoryConversationStore(UserContext());
        await store.CreateAsync(CreateRecord());

        var result = await store.AppendMessagesAsync(
            "t1", "c1", 999, new[] { CreateMessage("m1", 1) });

        Assert.False(result.Success);
        Assert.NotNull(result.ConflictReason);
    }

    [Fact]
    public async Task AppendMessages_IdempotencyKey_Deduplicates()
    {
        var store = new InMemoryConversationStore(UserContext());
        await store.CreateAsync(CreateRecord());

        await store.AppendMessagesAsync("t1", "c1", 1,
            new[] { CreateMessage("m1", 1, idempotencyKey: "idem-1") });

        var result = await store.AppendMessagesAsync("t1", "c1", 2,
            new[]
            {
                CreateMessage("m1b", 1, idempotencyKey: "idem-1"),
                CreateMessage("m2", 2, idempotencyKey: "idem-2")
            });

        Assert.True(result.Success);
        Assert.Equal(1, result.SkippedDuplicateCount);
        Assert.Equal(2, result.NewMessageCount);
    }

    [Fact]
    public async Task GetMessagesAsync_ReturnsLastNMessages()
    {
        var store = new InMemoryConversationStore(UserContext());
        await store.CreateAsync(CreateRecord());
        await store.AppendMessagesAsync("t1", "c1", 1, new[]
        {
            CreateMessage("m1", 1, content: "a"),
            CreateMessage("m2", 2, content: "b"),
            CreateMessage("m3", 3, content: "c"),
        });

        var messages = await store.GetMessagesAsync("t1", "c1", 2);

        Assert.Equal(2, messages.Count);
        Assert.Equal("b", messages[0].Content);
        Assert.Equal("c", messages[1].Content);
    }

    [Fact]
    public async Task ListConversations_ExcludesSoftDeleted()
    {
        var store = new InMemoryConversationStore(UserContext());
        await store.CreateAsync(CreateRecord(conversationId: "keep"));
        await store.CreateAsync(CreateRecord(conversationId: "drop"));
        await store.SoftDeleteAsync("t1", "drop");

        var list = await store.ListConversationsAsync("t1", 0, 10);

        Assert.Single(list);
        Assert.Equal("keep", list[0].ConversationId);
    }

    [Fact]
    public async Task ListAndSearchConversations_InternalAndChannelRecords_ExcludeFromUserResults()
    {
        ICurrentUserContext user = UserContext();
        var store = new InMemoryConversationStore(user);
        await store.CreateAsync(CreateRecord(conversationId: "user"));
        await store.CreateAsync(CreateRecord(
            conversationId: "intent",
            type: ConversationType.Internal,
            ownerRole: ConversationOwnerRole.Service));
        await store.CreateAsync(CreateRecord(
            conversationId: "channel",
            userId: "channel-service",
            type: ConversationType.Channel,
            ownerRole: ConversationOwnerRole.System));
        await store.AppendMessagesAsync(
            "t1",
            "user",
            1,
            [CreateMessage("user-message", 1, content: "needle")]);
        await store.AppendMessagesAsync(
            "t1",
            "intent",
            1,
            [CreateMessage("intent-message", 1, content: "needle")]);
        await store.AppendMessagesAsync(
            "t1",
            "channel",
            1,
            [CreateMessage("channel-message", 1, content: "needle")]);

        IReadOnlyList<ConversationRecord> list = await store.ListConversationsAsync("t1", 0, 10);
        IReadOnlyList<ConversationRecord> search = await store.SearchConversationsAsync("t1", "needle", 0, 10);

        Assert.Equal("user", Assert.Single(list).ConversationId);
        Assert.Equal("user", Assert.Single(search).ConversationId);
        Assert.Null(await store.GetRecordAsync("other-tenant", "channel"));
    }

    [Fact]
    public async Task GetRecordAsync_InternalRecordThroughUserQuery_ReturnsNull()
    {
        ICurrentUserContext user = UserContext();
        var store = new InMemoryConversationStore(user);
        await store.CreateAsync(CreateRecord(
            conversationId: "intent",
            type: ConversationType.Internal,
            ownerRole: ConversationOwnerRole.Service));
        var query = new ConversationQueryService(store, user);

        ConversationRecord? record = await query.GetRecordAsync("t1", "intent");

        Assert.Null(record);
    }

    [Fact]
    public async Task OpenAsync_InternalExecution_PersistsSeparateConversationBoundary()
    {
        ICurrentUserContext user = UserContext();
        var store = new InMemoryConversationStore(user);
        var sessions = new ConversationSessionStore(
            store,
            Options.Create(new ConversationStoreOptions()));
        var context = new ConversationContext(
            "intent-execution",
            "t1",
            "u1",
            "intent-router",
            "trace-1",
            ConversationType.Internal,
            ConversationOwnerRole.Service);

        await sessions.OpenAsync(
            context,
            "intent-router",
            "route this request",
            CancellationToken.None);

        ConversationRecord? stored = await store.GetRecordAsync("t1", "intent-execution");
        Assert.NotNull(stored);
        Assert.Equal(ConversationType.Internal, stored.Type);
        Assert.Equal(ConversationOwnerRole.Service, stored.OwnerRole);
        Assert.Empty(await store.ListConversationsAsync("t1", 0, 10));
    }

    [Fact]
    public async Task SearchConversations_MatchesTitleAndContent()
    {
        var store = new InMemoryConversationStore(UserContext());
        await store.CreateAsync(CreateRecord(conversationId: "c1"));
        await store.AppendMessagesAsync("t1", "c1", 1,
            new[] { CreateMessage("m1", 1, content: "unique needle text") });

        var results = await store.SearchConversationsAsync("t1", "needle", 0, 10);

        Assert.Single(results);
    }

    [Fact]
    public async Task UpdateStatus_ExpectedVersion_Succeeds()
    {
        var store = new InMemoryConversationStore(UserContext());
        await store.CreateAsync(CreateRecord());

        var ok = await store.UpdateStatusAsync("t1", "c1", ConversationStatus.Completed, 1);

        Assert.True(ok);
        var record = await store.GetRecordAsync("t1", "c1");
        Assert.Equal(ConversationStatus.Completed, record!.Status);
        Assert.Equal(2, record.Version);
    }

    [Fact]
    public async Task UpdateStatus_WrongVersion_Fails()
    {
        var store = new InMemoryConversationStore(UserContext());
        await store.CreateAsync(CreateRecord());

        var ok = await store.UpdateStatusAsync("t1", "c1", ConversationStatus.Completed, 5);
        Assert.False(ok);
    }
}
