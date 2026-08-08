using Microsoft.AspNetCore.Mvc;
using OpenAgent.Contracts.Content;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Routing;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Runtime.Agent;
using OpenAgent.Engine.Host.Attachments;
using OpenAgent.Engine.Host.Middleware;

namespace OpenAgent.Engine.Host.Extensions;

internal static class AttachmentEndpointExtensions
{
    internal static void MapAttachmentChat(this RouteGroupBuilder group)
    {
        group.MapPost("/chat/attachments", ExecuteAsync)
            .RequireAuthorization(GatewayPermissions.AgentExecute)
            .DisableAntiforgery()
            .WithName("ChatWithAttachments")
            .WithTags("Agent");

        group.MapPost("/chat/attachments/stream", ExecuteStreamAsync)
            .RequireAuthorization(GatewayPermissions.AgentExecute)
            .DisableAntiforgery()
            .WithName("ChatWithAttachmentsStream")
            .WithTags("Agent");
    }

    private static async Task<IResult> ExecuteAsync(
        [FromServices] AgentExecutor executor,
        [FromServices] AgentAttachmentReader attachmentReader,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        AgentRequest request = await CreateRequestAsync(
            context,
            attachmentReader,
            cancellationToken).ConfigureAwait(false);
        AgentResponse response = await executor.ExecuteAsync(
            request,
            context.GetAgentRequest().User,
            cancellationToken).ConfigureAwait(false);
        return Results.Ok(new ChatResponse { Message = response.Content });
    }

    private static async Task ExecuteStreamAsync(
        [FromServices] AgentExecutor executor,
        [FromServices] AgentAttachmentReader attachmentReader,
        [FromServices] ILogger<Program> logger,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        AgentRequest request = await CreateRequestAsync(
            context,
            attachmentReader,
            cancellationToken).ConfigureAwait(false);
        await AgentStreamWriter.WriteSseStreamAsync(
            context,
            executor.ExecuteStreamingAsync(
                request,
                context.GetAgentRequest().User,
                cancellationToken),
            request.TraceId!,
            logger,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<AgentRequest> CreateRequestAsync(
        HttpContext context,
        AgentAttachmentReader attachmentReader,
        CancellationToken cancellationToken)
    {
        IFormCollection form = await context.Request.ReadFormAsync(
            cancellationToken).ConfigureAwait(false);
        string message = form["message"].FirstOrDefault() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(message))
        {
            throw new AgentException(
                AgentErrorCode.MissingRequiredField,
                "Form field 'message' is required.");
        }

        IReadOnlyList<AgentAttachment> attachments = await attachmentReader.ReadAsync(
            form.Files,
            cancellationToken).ConfigureAwait(false);
        return new AgentRequest
        {
            Query = message,
            AgentId = ResolveAgentId(context.Request, form),
            ConversationId = form["conversationId"].FirstOrDefault()
                ?? context.Request.Headers["X-Conversation-Id"].FirstOrDefault(),
            TraceId = context.GetAgentRequest().TraceId,
            ClientType = ClientType.Web,
            Attachments = attachments
        };
    }

    internal static string? ResolveAgentId(HttpRequest request, IFormCollection form) =>
        request.Headers[AgentRoutingHeaders.ResolvedAgentId].FirstOrDefault()
        ?? form["agentId"].FirstOrDefault()
        ?? request.Headers["X-Agent-Id"].FirstOrDefault();
}
