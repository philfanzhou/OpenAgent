using Microsoft.AspNetCore.Mvc;
using OpenAgent.Contracts.Requests;
using OpenAgent.Core.Runtime.Agent;
using OpenAgent.Engine.Host.Middleware;

namespace OpenAgent.Engine.Host.Extensions;

internal static class AgentChatEndpointExtensions
{
    internal static void MapAgentChat(this RouteGroupBuilder group)
    {
        group.MapPost("/chat", ExecuteAsync)
            .WithName("Chat")
            .WithTags("Agent");

        group.MapPost("/chat/stream", ExecuteStreamAsync)
            .WithName("ChatStream")
            .WithTags("Agent");

    }

    private static async Task<IResult> ExecuteAsync(
        [FromBody] ChatRequest request,
        [FromServices] AgentExecutor executor,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        AgentRequest executionRequest = AgentEndpointRequestMapper.CreateAgentRequest(request, context);
        AgentResponse response = await executor.ExecuteAsync(
            executionRequest,
            context.GetAgentRequest().User,
            cancellationToken).ConfigureAwait(false);
        return Results.Ok(new ChatResponse { Message = response.Content });
    }

    private static async Task ExecuteStreamAsync(
        [FromBody] ChatRequest request,
        [FromServices] AgentExecutor executor,
        [FromServices] ILogger<Program> logger,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        AgentRequest executionRequest = AgentEndpointRequestMapper.CreateAgentRequest(request, context);
        await AgentStreamWriter.WriteSseStreamAsync(
            context,
            executor.ExecuteStreamingAsync(
                executionRequest,
                context.GetAgentRequest().User,
                cancellationToken),
            executionRequest.TraceId!,
            executionRequest.ConversationId!,
            logger,
            cancellationToken).ConfigureAwait(false);
    }
}
