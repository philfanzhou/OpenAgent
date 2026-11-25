using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Runtime;
using OpenAgent.Contracts.Security;
using OpenAgent.Engine.Host.Endpoints;
using OpenAgent.Engine.Host.Middleware;
using OpenAgent.Engine.Host.Streaming;
using OpenAgent.Engine.Hosting.Errors;

namespace OpenAgent.Engine.Host.Endpoints.Conversations;

internal static class ConversationEndpoints
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

        group.MapGet("/conversations/{conversationId}/stream", ResumeStreamAsync)
            .WithName("ResumeConversationStream")
            .WithTags("Conversation");

        group.MapPost("/conversations/{conversationId}/cancel", CancelAsync)
            .WithName("CancelConversation")
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

    private static async Task<IResult> DeleteAsync(
        [FromServices] IConversationQueryService queryService,
        [FromServices] ConversationRunRegistry runs,
        HttpContext context,
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        var access = await FindOwnedAsync(queryService, runs, context, conversationId, cancellationToken)
            .ConfigureAwait(false);
        if (access.Failure != null)
            return access.Failure;

        if (access.Run != null)
        {
            access.Run.Cancel();
            await access.Run.Completion.ConfigureAwait(false);
        }

        bool deleted = await queryService.SoftDeleteAsync(
            AgentEndpointRequestMapper.RequireTenant(context),
            conversationId,
            cancellationToken).ConfigureAwait(false);
        return deleted ? TypedResults.NoContent() : TypedResults.NotFound();
    }

    private static async Task<IResult> GetAsync(
        [FromServices] IConversationQueryService queryService,
        [FromServices] ConversationRunRegistry runs,
        [FromServices] IFileAssetService files,
        HttpContext context,
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        var access = await FindOwnedAsync(queryService, runs, context, conversationId, cancellationToken)
            .ConfigureAwait(false);
        if (access.Failure != null)
            return access.Failure;

        if (access.Run == null)
            return TypedResults.Ok(access.Record!);

        ConversationRun run = access.Run;
        ConversationRecord record = access.Record ?? new ConversationRecord
        {
            ConversationId = conversationId,
            TenantId = run.User.TenantId!,
            UserId = run.User.UserId,
            Type = run.Request.ConversationType,
            AgentId = run.Request.AgentId,
            TraceId = run.Request.TraceId,
            CreatedAt = run.StartedAt
        };
        List<ConversationMessage> messages = [.. record.Messages];
        string inputId = run.Request.TraceId + "-input";
        if (!messages.Any(message => string.Equals(message.MessageId, inputId, StringComparison.Ordinal)
            || (string.Equals(message.Role, "user", StringComparison.Ordinal)
                && string.Equals(message.TraceId, run.Request.TraceId, StringComparison.Ordinal))))
        {
            List<MessageFileMetadata> attachments = [];
            FileAssetScope scope = new()
            {
                TenantId = run.User.TenantId!,
                UserId = run.User.UserId,
                ConversationId = conversationId
            };
            foreach (string fileId in run.Request.FileIds)
            {
                FileAsset? asset = await files.GetAsync(fileId, scope, cancellationToken).ConfigureAwait(false);
                attachments.Add(new MessageFileMetadata(
                    fileId,
                    asset?.FileName ?? fileId,
                    asset?.MediaType ?? "application/octet-stream",
                    asset?.Length ?? 0,
                    asset?.ObjectKey));
            }

            messages.Add(new ConversationMessage
            {
                MessageId = inputId,
                Sequence = messages.Count == 0 ? 1 : messages.Max(message => message.Sequence) + 1,
                Role = "user",
                Content = run.Request.Query,
                Timestamp = run.StartedAt,
                TraceId = run.Request.TraceId,
                FileIds = run.Request.FileIds,
                Metadata = attachments.Count == 0 ? null : new ConversationMessageMetadata { Files = attachments }
            });
        }

        if (run.Completion.IsCompleted)
        {
            var finalized = await FindOwnedAsync(queryService, runs, context, conversationId, cancellationToken)
                .ConfigureAwait(false);
            if (finalized.Failure != null) return finalized.Failure;
            if (finalized.Run == null) return TypedResults.Ok(finalized.Record!);
        }

        return TypedResults.Ok(record with
        {
            Status = ConversationStatus.Running,
            TraceId = run.Request.TraceId,
            UpdatedAt = run.StartedAt,
            LastMessageAt = run.StartedAt,
            MessageCount = messages.Count,
            Messages = messages
        });
    }

    private static async Task<IResult> ResumeStreamAsync(
        [FromServices] IConversationQueryService queryService,
        [FromServices] ConversationRunRegistry runs,
        [FromServices] ILogger<Program> logger,
        HttpContext context,
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        var access = await FindOwnedAsync(queryService, runs, context, conversationId, cancellationToken)
            .ConfigureAwait(false);
        if (access.Failure != null)
            return access.Failure;
        if (access.Run == null)
            return TypedResults.NoContent();

        context.Features.Get<IHttpActivityFeature>()?.Activity.SetTag(AuditAttributeNames.ConversationId, conversationId);
        await AgentStreamWriter.WriteSseStreamAsync(
            context,
            access.Run.ReadAsync(cancellationToken),
            access.Run.Request.TraceId!,
            conversationId,
            logger,
            cancellationToken).ConfigureAwait(false);
        return TypedResults.Empty;
    }

    private static async Task<IResult> CancelAsync(
        [FromServices] IConversationQueryService queryService,
        [FromServices] ConversationRunRegistry runs,
        HttpContext context,
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        var access = await FindOwnedAsync(queryService, runs, context, conversationId, cancellationToken)
            .ConfigureAwait(false);
        if (access.Failure != null)
            return access.Failure;
        if (access.Run != null)
        {
            access.Run.Cancel();
            await access.Run.Completion.ConfigureAwait(false);
        }

        return TypedResults.NoContent();
    }

    private static async Task<(ConversationRecord? Record, ConversationRun? Run, IResult? Failure)> FindOwnedAsync(
        IConversationQueryService queryService,
        ConversationRunRegistry runs,
        HttpContext context,
        string conversationId,
        CancellationToken cancellationToken)
    {
        string tenantId = AgentEndpointRequestMapper.RequireTenant(context);
        ConversationRun? observedRun = runs.Find(tenantId, conversationId);
        ConversationRecord? record = await queryService.GetRecordAsync(
            tenantId, conversationId, cancellationToken).ConfigureAwait(false);
        ConversationRun? run = runs.Find(tenantId, conversationId);
        if (observedRun != null && run == null)
        {
            record = await queryService.GetRecordAsync(tenantId, conversationId, cancellationToken)
                .ConfigureAwait(false);
        }
        if ((record == null && run == null)
            || record is { IsDeletedByUser: true }
            || (record != null && record.Type != ConversationType.User)
            || (run != null && run.Request.ConversationType != ConversationType.User))
        {
            return (null, null, TypedResults.NotFound());
        }

        string userId = context.GetAgentRequest().User.UserId;
        if ((record != null
                && (!string.Equals(record.TenantId, tenantId, StringComparison.Ordinal)
                    || !string.Equals(record.UserId, userId, StringComparison.Ordinal)))
            || (run != null
                && (!string.Equals(run.User.TenantId, tenantId, StringComparison.Ordinal)
                    || !string.Equals(run.User.UserId, userId, StringComparison.Ordinal))))
        {
            return (null, null, TypedResults.Forbid());
        }

        return (record, run, null);
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
        string tenantId = AgentEndpointRequestMapper.RequireTenant(context);
        ConversationRecord? record = await queryService.GetRecordAsync(
            tenantId,
            conversationId,
            cancellationToken).ConfigureAwait(false);
        if (record == null || record.IsDeletedByUser || record.Type != ConversationType.User)
            return TypedResults.NotFound();

        string userId = context.GetAgentRequest().User.UserId;
        if (!string.Equals(record.TenantId, tenantId, StringComparison.Ordinal)
            || !string.Equals(record.UserId, userId, StringComparison.Ordinal))
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
