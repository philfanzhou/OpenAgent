using Microsoft.AspNetCore.Http.HttpResults;
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
            .WithTags("Agent Provider")
            .WithSummary("校验 Provider 会话归属");
    }

    internal static async Task<Results<NoContent, NotFound, UnauthorizedHttpResult>> ResolveConversationAsync(
        [FromServices] IConversationQueryService queryService,
        HttpContext context,
        string conversationId,
        CancellationToken cancellationToken)
    {
        IAgentUserContext serviceUser = context.GetAgentRequest().User;
        if (!serviceUser.IsAuthenticated)
        {
            return TypedResults.Unauthorized();
        }

        if (string.IsNullOrWhiteSpace(serviceUser.TenantId))
        {
            return TypedResults.Unauthorized();
        }

        ConversationRecord? record = await queryService.GetRecordAsync(
            serviceUser.TenantId,
            conversationId,
            cancellationToken).ConfigureAwait(false);
        return record != null
            && !record.IsDeletedByUser
            && record.Type == ConversationType.User
            && string.Equals(record.UserId, serviceUser.UserId, StringComparison.Ordinal)
            ? TypedResults.NoContent()
            : TypedResults.NotFound();
    }
}
