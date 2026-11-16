using OpenAgent.Engine.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Security;
using OpenAgent.Engine.Host.Endpoints.Conversations;
using OpenAgent.Engine.Host.Middleware;
using Xunit;

namespace OpenAgent.Engine.Tests.Hosting;

public sealed class ConversationRouteTests
{
    [Fact]
    public async Task Delete_WithoutLlmProfileQuery_DeletesOwnedConversation()
    {
        var query = new Mock<IConversationQueryService>();
        query.Setup(value => value.GetRecordAsync("tenant-1", "conversation-1", Moq.It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConversationRecord
            {
                ConversationId = "conversation-1", TenantId = "tenant-1",
                UserId = "user-1", AgentId = "agent-1", Type = ConversationType.User
            });
        query.Setup(value => value.SoftDeleteAsync("tenant-1", "conversation-1", Moq.It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(query.Object);
        await using WebApplication app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Features.Set(CreateContext("tenant-1", "user-1").Features.Get<AgentRequestFeature>());
            await next(context);
        });
        app.MapGroup("/api/v1/agent").MapConversations();
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using HttpResponseMessage response = await client.DeleteAsync("/api/v1/agent/conversations/conversation-1");
        Assert.Equal(System.Net.HttpStatusCode.NoContent, response.StatusCode);
        query.Verify(value => value.SoftDeleteAsync("tenant-1", "conversation-1", Moq.It.IsAny<CancellationToken>()), Moq.Times.Once);
        using HttpResponseMessage compact = await client.PostAsync(
            "/api/v1/agent/conversations/conversation-1/compact?llmProfileId=profile-1", null);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, compact.StatusCode);
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
