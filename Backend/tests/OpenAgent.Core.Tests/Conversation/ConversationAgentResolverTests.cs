using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Conversation;
using OpenAgent.Core.Conversation.Store;
using Xunit;

namespace OpenAgent.Core.Tests.Conversation;

public class ConversationAgentResolverTests
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

    [Fact]
    public async Task ResolveContextAsync_ExistingConversation_ReturnsPersistedModelOverride()
    {
        var store = new InMemoryConversationStore(UserContext());
        ConversationRecord record = CreateRecord("user-1", "finance");
        record.ModelOverride = new LlmModelSelection
        {
            Provider = "provider-1",
            ModelId = "model-1"
        };
        await store.CreateAsync(record);
        var resolver = new ConversationAgentResolver(store);

        ConversationResolution result = await resolver.ResolveContextAsync(
            CreateRequest(conversationId: "conversation-1"),
            CreateUser(),
            CancellationToken.None);

        Assert.Equal("finance", result.AgentId);
        Assert.Equal("provider-1", result.ModelOverride?.Provider);
        Assert.Equal("model-1", result.ModelOverride?.ModelId);
    }

    [Theory]
    [InlineData("another-user", false)]
    [InlineData("user-1", true)]
    public async Task ResolveAsync_InaccessibleConversation_ReturnsPermissionDenied(
        string ownerId,
        bool deleted)
    {
        var store = new InMemoryConversationStore(UserContext());
        ConversationRecord record = CreateRecord(ownerId, "finance");
        record.IsDeletedByUser = deleted;
        await store.CreateAsync(record);
        var resolver = new ConversationAgentResolver(store);

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
