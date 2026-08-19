using Microsoft.AspNetCore.Mvc;
using OpenAgent.Contracts.Conversation;
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

        if (string.IsNullOrWhiteSpace(serviceUser.TenantId))
        {
            return Results.Unauthorized();
        }

        ConversationRecord? record = await queryService.GetRecordAsync(
            serviceUser.TenantId,
            conversationId,
            cancellationToken).ConfigureAwait(false);
        return record != null
            && !record.IsDeletedByUser
            && record.Type == ConversationType.User
            && record.OwnerRole == ConversationOwnerRole.User
            && string.Equals(record.UserId, serviceUser.UserId, StringComparison.Ordinal)
            ? Results.NoContent()
            : Results.NotFound();
    }
}
