using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Security;
using OpenAgent.Engine.Host.Middleware;
using OpenAgent.Hosting.Errors;

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

        group.MapGet("/conversations/{conversationId}", GetAsync)
            .WithName("GetConversation")
            .WithTags("Conversation");

        group.MapDelete("/conversations/{conversationId}", DeleteAsync)
            .WithName("DeleteConversation")
            .WithTags("Conversation");

        group.MapPost("/conversations/{conversationId}/compact", CompactAsync)
            .WithName("CompactConversation")
            .WithTags("Conversation");

        group.MapGet("/conversations/{conversationId}/llm-interactions", ListInteractionsAsync)
            .WithName("ListConversationLlmInteractions")
            .WithTags("Conversation");
    }

    private static async Task<IReadOnlyList<ConversationRecord>> ListAsync(
        [FromServices] IConversationQueryService queryService,
        HttpContext context,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 20,
        CancellationToken cancellationToken = default)
    {
        return await queryService.ListConversationsAsync(
            AgentEndpointRequestMapper.RequireTenant(context),
            skip,
            take,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Results<Ok<IReadOnlyList<ConversationRecord>>, ProblemHttpResult>> SearchAsync(
        [FromServices] IConversationQueryService queryService,
        HttpContext context,
        [FromQuery] string keyword = "",
        [FromQuery] int skip = 0,
        [FromQuery] int take = 20,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(keyword))
        {
            return TypedResults.Problem(AgentProblemDetails.Invalid("keyword is required", context));
        }

        IReadOnlyList<ConversationRecord> results = await queryService.SearchConversationsAsync(
            AgentEndpointRequestMapper.RequireTenant(context),
            keyword,
            skip,
            take,
            cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(results);
    }

    private static async Task<Results<NoContent, NotFound, ForbidHttpResult>> DeleteAsync(
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
            return TypedResults.NotFound();
        }

        string userId = context.GetAgentRequest().User.UserId;
        if (!string.Equals(record.UserId, userId, StringComparison.OrdinalIgnoreCase))
        {
            return TypedResults.Forbid();
        }

        bool deleted = await queryService.SoftDeleteAsync(
            tenantId,
            conversationId,
            cancellationToken).ConfigureAwait(false);
        return deleted ? TypedResults.NoContent() : TypedResults.NotFound();
    }

    private static async Task<Results<Ok<ConversationRecord>, NotFound, ForbidHttpResult>> GetAsync(
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
            return TypedResults.NotFound();

        string userId = context.GetAgentRequest().User.UserId;
        return string.Equals(record.UserId, userId, StringComparison.OrdinalIgnoreCase)
            ? TypedResults.Ok(record)
            : TypedResults.Forbid();
    }

    internal static async Task<Results<Ok<ContextSummary>, NotFound, ForbidHttpResult>> CompactAsync(
        [FromServices] IConversationQueryService queryService,
        [FromServices] IConversationCompactionService compactionService,
        HttpContext context,
        string conversationId,
        [FromQuery] string llmProfileId,
        CancellationToken cancellationToken = default)
    {
        string tenantId = AgentEndpointRequestMapper.RequireTenant(context);
        ConversationRecord? record = await queryService.GetRecordAsync(
            tenantId,
            conversationId,
            cancellationToken).ConfigureAwait(false);
        if (record == null)
        {
            return TypedResults.NotFound();
        }

        IAgentUserContext user = context.GetAgentRequest().User;
        if (!string.Equals(record.TenantId, tenantId, StringComparison.Ordinal)
            || !string.Equals(record.UserId, user.UserId, StringComparison.Ordinal))
        {
            return TypedResults.Forbid();
        }

        ContextSummary result = await compactionService.CompactAsync(
            tenantId,
            conversationId,
            llmProfileId,
            user,
            context.GetAgentRequest().TraceId,
            cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(result);
    }

    internal static async Task<Results<Ok<IReadOnlyList<LlmInteractionRecord>>, NotFound, ForbidHttpResult>> ListInteractionsAsync(
        [FromServices] IConversationQueryService queryService,
        [FromServices] ILlmInteractionStore interactions,
        HttpContext context,
        string conversationId,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken cancellationToken = default)
    {
        ConversationRecord? record = await queryService.GetRecordAsync(
            AgentEndpointRequestMapper.RequireTenant(context),
            conversationId,
            cancellationToken).ConfigureAwait(false);
        if (record == null || record.Type != ConversationType.User)
            return TypedResults.NotFound();

        string userId = context.GetAgentRequest().User.UserId;
        if (!string.Equals(record.UserId, userId, StringComparison.OrdinalIgnoreCase))
            return TypedResults.Forbid();

        IReadOnlyList<LlmInteractionRecord> logs = await interactions.ListAsync(
            record.TenantId,
            conversationId,
            skip,
            Math.Clamp(take, 1, 200),
            cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(logs);
    }

}
