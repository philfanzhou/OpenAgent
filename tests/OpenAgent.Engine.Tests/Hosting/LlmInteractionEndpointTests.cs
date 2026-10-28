using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Moq;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Security;
using OpenAgent.Engine.Host.Extensions;
using OpenAgent.Engine.Host.Middleware;
using Xunit;

namespace OpenAgent.Engine.Tests.Hosting;

public sealed class LlmInteractionEndpointTests
{
    [Theory]
    [InlineData("user-1", StatusCodes.Status200OK, 1)]
    [InlineData("user-2", StatusCodes.Status403Forbidden, 0)]
    public async Task ListInteractions_EnforcesUserOwnership(
        string requestUser,
        int expectedStatus,
        int expectedListCalls)
    {
        var query = new Mock<IConversationQueryService>();
        query.Setup(value => value.GetRecordAsync("tenant-1", "conversation-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConversationRecord
            {
                ConversationId = "conversation-1",
                TenantId = "tenant-1",
                UserId = "user-1",
                Type = ConversationType.User
            });
        var interactions = new Mock<ILlmInteractionStore>();
        interactions.Setup(value => value.ListAsync(
                "tenant-1",
                "conversation-1",
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<LlmInteractionRecord>)[]);
        DefaultHttpContext context = CreateContext("tenant-1", requestUser);

        Results<Ok<IReadOnlyList<LlmInteractionRecord>>, NotFound, ForbidHttpResult> result = await ConversationEndpointExtensions.ListInteractionsAsync(
            query.Object,
            interactions.Object,
            context,
            "conversation-1",
            skip: 0,
            take: 50);

        int? status = result.Result is ForbidHttpResult
            ? StatusCodes.Status403Forbidden
            : Assert.IsAssignableFrom<IStatusCodeHttpResult>(result.Result).StatusCode;
        Assert.Equal(expectedStatus, status);
        interactions.Verify(
            value => value.ListAsync(
                "tenant-1",
                "conversation-1",
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Exactly(expectedListCalls));
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
}
