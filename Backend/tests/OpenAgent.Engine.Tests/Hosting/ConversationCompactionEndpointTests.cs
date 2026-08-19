using Microsoft.AspNetCore.Http;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Security;
using OpenAgent.Engine.Host.Extensions;
using OpenAgent.Engine.Host.Middleware;
using Xunit;

namespace OpenAgent.Engine.Tests.Hosting;

public sealed class ConversationCompactionEndpointTests
{
    [Theory]
    [InlineData("tenant-1", "user-1", "tenant-1", "user-1", StatusCodes.Status200OK, 1)]
    [InlineData("tenant-1", "user-2", "tenant-1", "user-1", StatusCodes.Status403Forbidden, 0)]
    [InlineData("tenant-1", "user-1", "tenant-2", "user-1", StatusCodes.Status403Forbidden, 0)]
    public async Task CompactAsync_OwnershipScope_EnforcesUserAndTenant(
        string requestTenant,
        string requestUser,
        string recordTenant,
        string recordUser,
        int expectedStatus,
        int expectedCalls)
    {
        var query = new QueryService(new ConversationRecord
        {
            ConversationId = "conversation-1",
            TenantId = recordTenant,
            UserId = recordUser,
            AgentId = "agent-1"
        });
        var compaction = new CompactionService();
        DefaultHttpContext context = CreateContext(requestTenant, requestUser);

        IResult result = await ConversationEndpointExtensions.CompactAsync(
            query,
            compaction,
            context,
            "conversation-1",
            CancellationToken.None);

        int? status = result is Microsoft.AspNetCore.Http.HttpResults.ForbidHttpResult
            ? StatusCodes.Status403Forbidden
            : Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode;
        Assert.Equal(expectedStatus, status);
        Assert.Equal(expectedCalls, compaction.Calls);
    }

    private static DefaultHttpContext CreateContext(string tenantId, string userId)
    {
        var context = new DefaultHttpContext();
        context.Features.Set(new AgentRequestFeature(
            "trace-1",
            new AgentUserContext
            {
                TenantId = tenantId,
                UserId = userId,
                IsAuthenticated = true
            }));
        return context;
    }

    private sealed class CompactionService : IConversationCompactionService
    {
        public int Calls { get; private set; }

        public Task<ContextSummary> CompactAsync(
            string tenantId,
            string conversationId,
            IAgentUserContext user,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new ContextSummary
            {
                CompressionId = "compression-1",
                Strategy = "truncation",
                Trigger = "Manual",
                Status = "Succeeded"
            });
        }
    }

    private sealed class QueryService(ConversationRecord record) : IConversationQueryService
    {
        public Task<ConversationRecord?> GetRecordAsync(
            string tenantId,
            string conversationId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ConversationRecord?>(record);

        public Task<IReadOnlyList<ConversationRecord>> ListConversationsAsync(
            string tenantId, int skip, int take, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ConversationRecord>>([]);

        public Task<IReadOnlyList<ConversationRecord>> SearchConversationsAsync(
            string tenantId, string keyword, int skip, int take, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ConversationRecord>>([]);

        public Task<bool> SoftDeleteAsync(
            string tenantId, string conversationId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }
}
