using Microsoft.AspNetCore.Mvc;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Security;
using OpenAgent.Hosting.Authentication;
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
        if (!serviceUser.IsAuthenticated
            || !serviceUser.Claims.TryGetValue(
                AgentDelegationTokenClaims.AuthenticationMode,
                out string? authenticationMode)
            || !string.Equals(
                authenticationMode,
                AgentDelegationTokenClaims.ProviderDelegation,
                StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(serviceUser.TenantId))
        {
            return Results.Unauthorized();
        }

        string tenantId = serviceUser.TenantId;

        ConversationRecord? record = await queryService.GetRecordAsync(
            tenantId,
            conversationId,
            cancellationToken).ConfigureAwait(false);
        return record != null
            && !record.IsDeletedByUser
            && string.Equals(record.UserId, serviceUser.UserId, StringComparison.Ordinal)
            ? Results.NoContent()
            : Results.NotFound();
    }
}
