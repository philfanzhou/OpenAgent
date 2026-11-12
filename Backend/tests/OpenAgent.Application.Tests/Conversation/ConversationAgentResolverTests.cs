using Moq;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using OpenAgent.Application.Conversation;
using OpenAgent.Application.Conversation.Store;
using Xunit;

namespace OpenAgent.Application.Tests.Conversation;

public class ConversationAgentResolverTests
{
    private static ICurrentUserContext UserContext(string userId = "user-1") => new FakeUserContext(userId);

    private sealed class FakeUserContext(string userId) : ICurrentUserContext
    {
        public string UserId => userId;
        public string? TenantId => "tenant-1";
        public bool IsAuthenticated => true;
        public IReadOnlyList<string> Roles => [];
        public bool IsInRole(string role) => false;
    }

    [Fact]
    public async Task ResolveAsync_ExplicitAgent_DoesNotReadConversation()
    {
        var resolver = new ConversationAgentResolver(new InMemoryConversationStore(UserContext()));

        string? agentId = await resolver.ResolveAsync(
            CreateRequest(agentId: "support", conversationId: "missing"),
            CreateUser(),
            CancellationToken.None);

        Assert.Equal("support", agentId);
    }

    [Fact]
    public async Task ResolveAsync_ExistingConversation_ReturnsBoundAgent()
    {
        var store = new InMemoryConversationStore(UserContext());
        await store.CreateAsync(CreateRecord("user-1", "finance"));
        var resolver = new ConversationAgentResolver(store);

        string? agentId = await resolver.ResolveAsync(
            CreateRequest(conversationId: "conversation-1"),
            CreateUser(),
            CancellationToken.None);

        Assert.Equal("finance", agentId);
    }

    [Fact]
    public async Task ResolveAsync_MissingConversation_ReturnsNull()
    {
        var resolver = new ConversationAgentResolver(new InMemoryConversationStore(UserContext()));

        string? agentId = await resolver.ResolveAsync(
            CreateRequest(conversationId: "missing"),
            CreateUser(),
            CancellationToken.None);

        Assert.Null(agentId);
    }

    [Theory]
    [InlineData("another-user", false)]
    [InlineData("user-1", true)]
    public async Task ResolveAsync_InaccessibleConversation_ReturnsPermissionDenied(
        string ownerId,
        bool deleted)
    {
        ConversationRecord record = CreateRecord(ownerId, "finance");
        record.IsDeletedByUser = deleted;
        Mock<IConversationStore> store = new();
        store.Setup(value => value.GetRecordAsync("tenant-1", "conversation-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(record);
        var resolver = new ConversationAgentResolver(store.Object);

        AgentException exception = await Assert.ThrowsAsync<AgentException>(() =>
            resolver.ResolveAsync(
                CreateRequest(conversationId: "conversation-1"),
                CreateUser(),
                CancellationToken.None));

        Assert.Equal(AgentErrorCode.PermissionDenied, exception.ErrorCode);
    }

    private static AgentRequest CreateRequest(
        string? agentId = null,
        string? conversationId = null) => new()
        {
            Query = "hello",
            AgentId = agentId,
            ConversationId = conversationId
        };

    private static AgentUserContext CreateUser() => new()
    {
        UserId = "user-1",
        TenantId = "tenant-1",
        IsAuthenticated = true
    };

    private static ConversationRecord CreateRecord(
        string userId,
        string agentId) => new()
        {
            ConversationId = "conversation-1",
            TenantId = "tenant-1",
            UserId = userId,
            AgentId = agentId
        };
}
