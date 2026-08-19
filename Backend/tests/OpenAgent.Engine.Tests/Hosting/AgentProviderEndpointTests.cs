using Microsoft.AspNetCore.Http;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Security;
using OpenAgent.Engine.Host.Extensions;
using OpenAgent.Engine.Host.Middleware;
using Xunit;

namespace OpenAgent.Engine.Tests.Hosting;

public class AgentProviderEndpointTests
{
    [Theory]
    [InlineData("tenant-1", "user-1", false, StatusCodes.Status204NoContent)]
    [InlineData("tenant-2", "user-1", false, StatusCodes.Status404NotFound)]
    [InlineData("tenant-1", "user-2", false, StatusCodes.Status404NotFound)]
    [InlineData("tenant-1", "user-1", true, StatusCodes.Status404NotFound)]
    public async Task ResolveConversationAsync_OwnershipScope_DoesNotLeakOtherConversations(
        string tenantId,
        string userId,
        bool deleted,
        int expectedStatus)
    {
        var query = new StubConversationQueryService(new ConversationRecord
        {
            ConversationId = "conversation-1",
            TenantId = "tenant-1",
            UserId = "user-1",
            IsDeletedByUser = deleted
        });
        DefaultHttpContext context = CreateContext(authenticated: true, tenantId, userId);

        IResult result = await AgentProviderEndpointExtensions.ResolveConversationAsync(
            query,
            context,
            "conversation-1",
            CancellationToken.None);

        IStatusCodeHttpResult status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(expectedStatus, status.StatusCode);
        Assert.Equal(tenantId, query.TenantId);
    }

    [Fact]
    public async Task ResolveConversationAsync_UnauthenticatedService_ReturnsUnauthorized()
    {
        DefaultHttpContext context = CreateContext(
            authenticated: false,
            "tenant-1",
            "user-1");

        IResult result = await AgentProviderEndpointExtensions.ResolveConversationAsync(
            new StubConversationQueryService(null),
            context,
            "conversation-1",
            CancellationToken.None);

        IStatusCodeHttpResult status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status401Unauthorized, status.StatusCode);
    }

    private static DefaultHttpContext CreateContext(
        bool authenticated,
        string tenantId,
        string userId)
    {
        var context = new DefaultHttpContext();
        context.Features.Set(new AgentRequestFeature(
            "trace-1",
            new AgentUserContext
            {
                UserId = userId,
                TenantId = tenantId,
                IsAuthenticated = authenticated
            }));
        return context;
    }

    private sealed class StubConversationQueryService(
        ConversationRecord? record) : IConversationQueryService
    {
        public string? TenantId { get; private set; }

        public Task<IReadOnlyList<ConversationRecord>> ListConversationsAsync(
            string tenantId,
            int skip,
            int take,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ConversationRecord>>([]);

        public Task<IReadOnlyList<ConversationRecord>> SearchConversationsAsync(
            string tenantId,
            string keyword,
            int skip,
            int take,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ConversationRecord>>([]);

        public Task<ConversationRecord?> GetRecordAsync(
            string tenantId,
            string conversationId,
            CancellationToken cancellationToken = default)
        {
            TenantId = tenantId;
            return Task.FromResult(
                record?.TenantId == tenantId && record.ConversationId == conversationId
                    ? record
                    : null);
        }

        public Task<bool> SoftDeleteAsync(
            string tenantId,
            string conversationId,
            CancellationToken cancellationToken = default) => Task.FromResult(false);
    }
}
