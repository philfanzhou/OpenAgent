using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Runtime.Agent;
using OpenAgent.Engine.Host.Middleware;

namespace OpenAgent.Engine.Host.Extensions;

internal static class EndpointExtensions
{
    private static readonly TimeSpan StreamHeartbeatInterval = TimeSpan.FromSeconds(15);

    public static IEndpointConventionBuilder MapAgentEndpoints(
        this IEndpointRouteBuilder endpoints,
        string pattern = "/api/v1/agent")
    {
        RouteGroupBuilder group = endpoints.MapGroup(pattern).RequireAuthorization();
        group.MapAttachmentChat();
        endpoints.MapManagementEndpoints();

        group.MapGet("/me", (HttpContext context) =>
        {
            IAgentUserContext user = context.GetAgentRequest().User;
            return Results.Ok(new
            {
                userId = user.UserId,
                tenantId = user.TenantId,
                roles = user.Roles,
                groups = user.Groups,
                audience = user.Audience,
                isAuthenticated = user.IsAuthenticated
            });
        })
        .WithName("CurrentAgentUser")
        .WithTags("Agent");

        group.MapPost("/chat", async (
            [FromBody] ChatRequest request,
            [FromServices] AgentExecutor executor,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            AgentRequest executionRequest = CreateAgentRequest(request, context);
            AgentResponse response = await executor.ExecuteAsync(
                executionRequest,
                context.GetAgentRequest().User,
                cancellationToken).ConfigureAwait(false);
            return Results.Ok(new ChatResponse { Message = response.Content });
        })
        .WithName("Chat")
        .WithTags("Agent");

        group.MapPost("/chat/stream", async (
            [FromBody] ChatRequest request,
            [FromServices] AgentExecutor executor,
            [FromServices] ILogger<Program> logger,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            AgentRequest executionRequest = CreateAgentRequest(request, context);
            await WriteNdjsonStreamAsync(
                context,
                executor.ExecuteStreamingAsync(
                    executionRequest,
                    context.GetAgentRequest().User,
                    cancellationToken),
                executionRequest.TraceId!,
                logger,
                cancellationToken).ConfigureAwait(false);
        })
        .WithName("ChatStream")
        .WithTags("Agent");

        group.MapPost("/chat/sse", async (
            [FromBody] ChatRequest request,
            [FromServices] AgentExecutor executor,
            [FromServices] ILogger<Program> logger,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            AgentRequest executionRequest = CreateAgentRequest(request, context);
            await WriteSseStreamAsync(
                context,
                executor.ExecuteStreamingAsync(
                    executionRequest,
                    context.GetAgentRequest().User,
                    cancellationToken),
                executionRequest.TraceId!,
                logger,
                cancellationToken).ConfigureAwait(false);
        })
        .WithName("ChatSse")
        .WithTags("Agent");

        group.MapPost("/chat/pipeline", async (
            [FromBody] AgentRequest request,
            [FromServices] AgentExecutor executor,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            AgentRequest executionRequest = ResolveRequest(request, context);
            AgentResponse response = await executor.ExecuteAsync(
                executionRequest,
                context.GetAgentRequest().User,
                cancellationToken).ConfigureAwait(false);
            return Results.Ok(response);
        })
        .WithName("ChatPipeline")
        .WithTags("Agent");

        group.MapGet("/agents", async (
            [FromServices] IAgentConfigProvider configProvider,
            CancellationToken cancellationToken) =>
        {
            IReadOnlyList<AgentSummary> agents = await configProvider.ListAgentsAsync(
                cancellationToken).ConfigureAwait(false);
            return Results.Ok(agents);
        })
        .WithName("ListAgents")
        .WithTags("Agent");

        group.MapGet("/conversations", async (
            [FromServices] IConversationQueryService queryService,
            HttpContext context,
            [FromQuery] int skip = 0,
            [FromQuery] int take = 20,
            CancellationToken cancellationToken = default) =>
        {
            IReadOnlyList<ConversationRecord> conversations = await queryService.ListConversationsAsync(
                RequireTenant(context),
                skip,
                take,
                cancellationToken).ConfigureAwait(false);
            return Results.Ok(conversations);
        })
        .WithName("ListConversations")
        .WithTags("Conversation");

        group.MapGet("/conversations/search", async (
            [FromServices] IConversationQueryService queryService,
            HttpContext context,
            [FromQuery] string keyword = "",
            [FromQuery] int skip = 0,
            [FromQuery] int take = 20,
            CancellationToken cancellationToken = default) =>
        {
            if (string.IsNullOrWhiteSpace(keyword))
            {
                return Results.BadRequest("keyword is required");
            }

            IReadOnlyList<ConversationRecord> results = await queryService.SearchConversationsAsync(
                RequireTenant(context),
                keyword,
                skip,
                take,
                cancellationToken).ConfigureAwait(false);
            return Results.Ok(results);
        })
        .WithName("SearchConversations")
        .WithTags("Conversation");

        group.MapGet("/conversations/{conversationId}", async (
            [FromServices] IConversationQueryService queryService,
            HttpContext context,
            string conversationId,
            CancellationToken cancellationToken = default) =>
        {
            ConversationRecord? record = await queryService.GetRecordAsync(
                RequireTenant(context),
                conversationId,
                cancellationToken).ConfigureAwait(false);
            if (record == null) return Results.NotFound();

            string userId = context.GetAgentRequest().User.UserId;
            return string.Equals(record.UserId, userId, StringComparison.OrdinalIgnoreCase)
                ? Results.Ok(record)
                : Results.Forbid();
        })
        .WithName("GetConversation")
        .WithTags("Conversation");

        group.MapDelete("/conversations/{conversationId}", async (
            [FromServices] IConversationQueryService queryService,
            HttpContext context,
            string conversationId,
            CancellationToken cancellationToken = default) =>
        {
            string tenantId = RequireTenant(context);
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
        })
        .WithName("DeleteConversation")
        .WithTags("Conversation");

        return group;
    }

    internal static async Task WriteNdjsonStreamAsync(
        HttpContext context,
        IAsyncEnumerable<AgentStreamEvent> events,
        string traceId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        StreamingResponseHeaders.ApplyNdjson(context);
        TokenUsage? usage = null;
        try
        {
            await using StreamingHeartbeat heartbeat = StreamingHeartbeat.Start(
                token => WriteNdjsonHeartbeatAsync(context, traceId, token),
                StreamHeartbeatInterval,
                logger,
                "agent-stream",
                traceId,
                cancellationToken);
            await foreach (AgentStreamEvent streamEvent in events.WithCancellation(cancellationToken))
            {
                if (streamEvent.Type == AgentStreamEventType.Usage)
                {
                    usage = streamEvent.Usage;
                    continue;
                }

                await heartbeat.WriteAsync(
                    token => WriteNdjsonEventAsync(
                        context,
                        StreamingPayloadFactory.CreateAgentEvent(streamEvent, traceId),
                        token),
                    cancellationToken).ConfigureAwait(false);
            }

            await heartbeat.WriteAsync(
                token => WriteNdjsonEventAsync(
                    context,
                    StreamingPayloadFactory.CreateDoneEvent(traceId, usage: usage),
                    token),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        {
            await WriteNdjsonEventAsync(
                context,
                StreamingPayloadFactory.CreateDoneEvent(traceId, "cancelled"),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (!context.RequestAborted.IsCancellationRequested)
        {
            await WriteNdjsonEventAsync(
                context,
                StreamingPayloadFactory.CreateErrorEvent(
                    StreamingPayloadFactory.CreateErrorPayload(exception, traceId),
                    traceId),
                CancellationToken.None).ConfigureAwait(false);
            await WriteNdjsonEventAsync(
                context,
                StreamingPayloadFactory.CreateDoneEvent(traceId, "error"),
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async Task WriteSseStreamAsync(
        HttpContext context,
        IAsyncEnumerable<AgentStreamEvent> events,
        string traceId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        StreamingResponseHeaders.ApplySse(context);
        TokenUsage? usage = null;
        try
        {
            await using StreamingHeartbeat heartbeat = StreamingHeartbeat.Start(
                async token =>
                {
                    await context.Response.WriteAsync(": heartbeat\n\n", token).ConfigureAwait(false);
                    await context.Response.Body.FlushAsync(token).ConfigureAwait(false);
                },
                StreamHeartbeatInterval,
                logger,
                "agent-sse",
                traceId,
                cancellationToken);
            await foreach (AgentStreamEvent streamEvent in events.WithCancellation(cancellationToken))
            {
                if (streamEvent.Type == AgentStreamEventType.Usage)
                {
                    usage = streamEvent.Usage;
                    continue;
                }

                string eventName = streamEvent.Type switch
                {
                    AgentStreamEventType.Reasoning => "reasoning",
                    AgentStreamEventType.ToolCall => "tool_call",
                    _ => "content"
                };
                string data = JsonSerializer.Serialize(new
                {
                    content = streamEvent.Content,
                    toolName = streamEvent.ToolName,
                    toolCallId = streamEvent.ToolCallId
                });
                await heartbeat.WriteAsync(
                    async token =>
                    {
                        await context.Response.WriteAsync(
                            $"event: {eventName}\ndata: {data}\n\n",
                            token).ConfigureAwait(false);
                        await context.Response.Body.FlushAsync(token).ConfigureAwait(false);
                    },
                    cancellationToken).ConfigureAwait(false);
            }

            string done = JsonSerializer.Serialize(new { done = true, usage });
            await context.Response.WriteAsync($"event: done\ndata: {done}\n\n", cancellationToken).ConfigureAwait(false);
            await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        {
            await context.Response.WriteAsync("event: done\ndata: [CANCELLED]\n\n", CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (!context.RequestAborted.IsCancellationRequested)
        {
            string error = JsonSerializer.Serialize(
                StreamingPayloadFactory.CreateErrorPayload(exception, traceId));
            await context.Response.WriteAsync($"event: error\ndata: {error}\n\n", CancellationToken.None).ConfigureAwait(false);
            await context.Response.WriteAsync("event: done\ndata: [ERROR]\n\n", CancellationToken.None).ConfigureAwait(false);
        }
    }

    internal static async Task WriteNdjsonEventAsync(
        HttpContext context,
        NdjsonStreamEvent payload,
        CancellationToken cancellationToken)
    {
        string line = JsonSerializer.Serialize(payload);
        await context.Response.WriteAsync(line + "\n", cancellationToken).ConfigureAwait(false);
        await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteNdjsonHeartbeatAsync(
        HttpContext context,
        string traceId,
        CancellationToken cancellationToken)
    {
        string line = JsonSerializer.Serialize(new { type = "heartbeat", traceId });
        await context.Response.WriteAsync(line + "\n", cancellationToken).ConfigureAwait(false);
        await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static AgentRequest CreateAgentRequest(ChatRequest request, HttpContext context)
    {
        AgentRequestFeature feature = context.GetAgentRequest();
        Dictionary<string, string>? externalContext = request.Context?
            .Where(item => !IsReservedChatContextKey(item.Key))
            .ToDictionary(item => item.Key, item => item.Value?.ToString() ?? string.Empty);
        return new AgentRequest
        {
            Query = request.Message,
            AgentId = ReadContextValue(request.Context, "agentId")
                ?? context.Request.Headers["X-Agent-Id"].FirstOrDefault(),
            ConversationId = ReadContextValue(request.Context, "conversationId")
                ?? context.Request.Headers["X-Conversation-Id"].FirstOrDefault(),
            TraceId = feature.TraceId,
            ClientType = ClientType.Web,
            ExternalContext = externalContext
        };
    }

    private static AgentRequest ResolveRequest(AgentRequest request, HttpContext context)
    {
        AgentRequestFeature feature = context.GetAgentRequest();
        return new AgentRequest
        {
            Query = request.Query,
            AgentId = request.AgentId ?? context.Request.Headers["X-Agent-Id"].FirstOrDefault(),
            ConversationId = request.ConversationId
                ?? context.Request.Headers["X-Conversation-Id"].FirstOrDefault(),
            TraceId = request.TraceId ?? feature.TraceId,
            ClientType = request.ClientType,
            IdempotencyKey = request.IdempotencyKey,
            ExternalContext = request.ExternalContext,
            Attachments = request.Attachments
        };
    }

    private static string? ReadContextValue(
        IReadOnlyDictionary<string, object>? context,
        string key)
    {
        if (context == null || !context.TryGetValue(key, out object? value))
        {
            return null;
        }

        return value is JsonElement element && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : value?.ToString();
    }

    private static bool IsReservedChatContextKey(string key) =>
        key.Equals("agentId", StringComparison.OrdinalIgnoreCase)
        || key.Equals("conversationId", StringComparison.OrdinalIgnoreCase)
        || key.Equals("traceId", StringComparison.OrdinalIgnoreCase);

    private static string RequireTenant(HttpContext context)
    {
        return context.GetAgentRequest().User.TenantId
            ?? throw new TenantDataIsolationException(null, null, "TenantId is required but not provided");
    }
}
