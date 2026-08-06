using Microsoft.AspNetCore.Mvc;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Security;
using OpenAgent.Engine.Host.Middleware;

namespace OpenAgent.Engine.Host.Extensions;

internal static class ConversationEndpointExtensions
{
    internal static void MapConversations(this RouteGroupBuilder group)
    {
        group.MapGet("/conversations", ListAsync)
            .WithName("ListConversations")
            .WithTags("Conversation");

        group.MapGet("/conversations/search", SearchAsync)
            .WithName("SearchConversations")
            .WithTags("Conversation");

        group.MapDelete("/conversations/{conversationId}", DeleteAsync)
            .WithName("DeleteConversation")
            .WithTags("Conversation");
    }

    private static async Task<IResult> ListAsync(
        [FromServices] IConversationQueryService queryService,
        HttpContext context,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 20,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ConversationRecord> conversations = await queryService.ListConversationsAsync(
            AgentEndpointRequestMapper.RequireTenant(context),
            skip,
            take,
            cancellationToken).ConfigureAwait(false);
        return Results.Ok(conversations);
    }

    private static async Task<IResult> SearchAsync(
        [FromServices] IConversationQueryService queryService,
        HttpContext context,
        [FromQuery] string keyword = "",
        [FromQuery] int skip = 0,
        [FromQuery] int take = 20,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(keyword))
        {
            return Results.BadRequest("keyword is required");
        }

        IReadOnlyList<ConversationRecord> results = await queryService.SearchConversationsAsync(
            AgentEndpointRequestMapper.RequireTenant(context),
            keyword,
            skip,
            take,
            cancellationToken).ConfigureAwait(false);
        return Results.Ok(results);
    }

    private static async Task<IResult> DeleteAsync(
        [FromServices] IConversationQueryService queryService,
        HttpContext context,
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        string tenantId = AgentEndpointRequestMapper.RequireTenant(context);
        ConversationRecord? record = await queryService.GetRecordAsync(
            tenantId,
            conversationId,
            cancellationToken).ConfigureAwait(false);
        if (record == null)
        {
            return Results.NotFound();
        }

        string userId = context.GetAgentRequest().User.UserId;
        if (!string.Equals(record.UserId, userId, StringComparison.OrdinalIgnoreCase))
        {
            return Results.Forbid();
        }

        bool deleted = await queryService.SoftDeleteAsync(
            tenantId,
            conversationId,
            cancellationToken).ConfigureAwait(false);
        return deleted ? Results.NoContent() : Results.NotFound();
    }
}
