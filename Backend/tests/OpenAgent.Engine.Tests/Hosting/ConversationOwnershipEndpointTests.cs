using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Security;
using OpenAgent.Engine.Host.Endpoints.Conversations;
using OpenAgent.Engine.Host.Middleware;
using Xunit;

namespace OpenAgent.Engine.Tests.Hosting;

public sealed class ConversationOwnershipEndpointTests
{
    [Theory]
    [InlineData("tenant-1", "user-1", false, ConversationType.User, HttpStatusCode.OK)]
    [InlineData("tenant-1", "user-2", false, ConversationType.User, HttpStatusCode.Forbidden)]
    [InlineData("tenant-1", "USER-1", false, ConversationType.User, HttpStatusCode.Forbidden)]
    [InlineData("tenant-2", "user-1", false, ConversationType.User, HttpStatusCode.Forbidden)]
    [InlineData("TENANT-1", "user-1", false, ConversationType.User, HttpStatusCode.Forbidden)]
    [InlineData("tenant-1", "user-1", true, ConversationType.User, HttpStatusCode.NotFound)]
    [InlineData("tenant-1", "user-1", false, ConversationType.Internal, HttpStatusCode.NotFound)]
    public async Task GetAndDelete_AllExecutionStatuses_EnforceOwnership(
        string tenantId, string userId, bool deleted, ConversationType type, HttpStatusCode expectedGet)
    {
        ConversationRecord record = new()
        {
            ConversationId = "conversation-1", TenantId = "tenant-1", UserId = "user-1",
            IsDeletedByUser = deleted, Type = type,
            Messages = [new() { MessageId = "private-message", Sequence = 1, Role = "user", Content = "owner-only-content" }]
        };
        Mock<IConversationQueryService> query = new();
        query.Setup(value => value.GetRecordAsync(tenantId, "conversation-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(record);
        query.Setup(value => value.SoftDeleteAsync(tenantId, "conversation-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthentication("test").AddCookie("test", options =>
        {
            options.Events.OnRedirectToAccessDenied = redirect =>
            {
                redirect.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            };
        });
        builder.Services.AddSingleton(query.Object);
        await using WebApplication app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Features.Set(new AgentRequestFeature("trace", new AgentUserContext
            {
                TenantId = tenantId, UserId = userId, IsAuthenticated = true
            }));
            await next(context);
        });
        app.MapGroup("/api/v1/agent").MapConversations();
        await app.StartAsync();
        using HttpClient client = new() { BaseAddress = new Uri(app.Urls.Single()) };

        foreach (ConversationStatus status in Enum.GetValues<ConversationStatus>())
        {
            record.Status = status;
            using HttpResponseMessage get = await client.GetAsync("/api/v1/agent/conversations/conversation-1");
            Assert.Equal(expectedGet, get.StatusCode);
            string content = await get.Content.ReadAsStringAsync();
            if (expectedGet == HttpStatusCode.OK)
            {
                Assert.Contains("owner-only-content", content);
            }
            else
            {
                Assert.DoesNotContain("owner-only-content", content);
            }

            using HttpResponseMessage remove = await client.DeleteAsync("/api/v1/agent/conversations/conversation-1");
            Assert.Equal(expectedGet == HttpStatusCode.OK ? HttpStatusCode.NoContent : expectedGet, remove.StatusCode);
        }
        query.Verify(value => value.SoftDeleteAsync(tenantId, "conversation-1", It.IsAny<CancellationToken>()),
            Times.Exactly(expectedGet == HttpStatusCode.OK ? Enum.GetValues<ConversationStatus>().Length : 0));
    }
}
