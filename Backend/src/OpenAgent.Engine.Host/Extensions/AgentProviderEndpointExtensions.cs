using Microsoft.AspNetCore.Mvc;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Routing;
using OpenAgent.Contracts.Security;
using OpenAgent.Engine.Host.Middleware;

namespace OpenAgent.Engine.Host.Extensions;

internal static class AgentProviderEndpointExtensions
{
    internal static void MapAgentProviderContract(this RouteGroupBuilder group)
    {
        group.MapGet("/provider/conversations/{conversationId}", ResolveConversationAsync)
            .WithName("ResolveProviderConversation")
            .WithTags("Agent Provider");
    }

    internal static async Task<IResult> ResolveConversationAsync(
        [FromServices] IConversationQueryService queryService,
        HttpContext context,
        string conversationId,
        CancellationToken cancellationToken)
    {
        IAgentUserContext serviceUser = context.GetAgentRequest().User;
        if (!serviceUser.IsAuthenticated)
        {
            return Results.Unauthorized();
        }

        string? tenantId = context.Request.Headers[AgentProviderHeaders.TenantId].FirstOrDefault();
        string? userId = context.Request.Headers[AgentProviderHeaders.UserId].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(userId))
        {
            return Results.BadRequest();
        }

        ConversationRecord? record = await queryService.GetRecordAsync(
            tenantId,
            conversationId,
            cancellationToken).ConfigureAwait(false);
        return record != null
            && !record.IsDeletedByUser
            && string.Equals(record.UserId, userId, StringComparison.Ordinal)
            ? Results.NoContent()
            : Results.NotFound();
    }
}
