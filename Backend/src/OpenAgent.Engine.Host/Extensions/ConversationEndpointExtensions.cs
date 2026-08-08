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
            .RequireAuthorization(GatewayPermissions.ConversationRead)
            .WithName("ListConversations")
            .WithTags("Conversation");

        group.MapGet("/conversations/search", SearchAsync)
            .RequireAuthorization(GatewayPermissions.ConversationRead)
            .WithName("SearchConversations")
            .WithTags("Conversation");

        group.MapGet("/conversations/{conversationId}", GetAsync)
            .RequireAuthorization(GatewayPermissions.ConversationRead)
            .WithName("GetConversation")
            .WithTags("Conversation");

        group.MapDelete("/conversations/{conversationId}", DeleteAsync)
            .RequireAuthorization(GatewayPermissions.ConversationDelete)
            .WithName("DeleteConversation")
            .WithTags("Conversation");

        group.MapPost("/conversations/{conversationId}/compact", CompactAsync)
            .WithName("CompactConversation")
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
        if (record == null
            || record.Type != ConversationType.User)
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

    private static async Task<IResult> GetAsync(
        [FromServices] IConversationQueryService queryService,
        HttpContext context,
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        ConversationRecord? record = await queryService.GetRecordAsync(
            AgentEndpointRequestMapper.RequireTenant(context),
            conversationId,
            cancellationToken).ConfigureAwait(false);
        if (record == null
            || record.Type != ConversationType.User)
            return Results.NotFound();

        string userId = context.GetAgentRequest().User.UserId;
        return string.Equals(record.UserId, userId, StringComparison.OrdinalIgnoreCase)
            ? Results.Ok(record)
            : Results.Forbid();
    }

    internal static async Task<IResult> CompactAsync(
        [FromServices] IConversationQueryService queryService,
        [FromServices] IConversationCompactionService compactionService,
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

        IAgentUserContext user = context.GetAgentRequest().User;
        if (!string.Equals(record.TenantId, tenantId, StringComparison.Ordinal)
            || !string.Equals(record.UserId, user.UserId, StringComparison.Ordinal))
        {
            return Results.Forbid();
        }

        ContextSummary result = await compactionService.CompactAsync(
            tenantId,
            conversationId,
            user,
            cancellationToken).ConfigureAwait(false);
        return Results.Ok(result);
    }

}
