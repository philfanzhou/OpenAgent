using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Conversation;
using OpenAgent.Core.Files;
using PlatformAgentResponse = OpenAgent.Contracts.Requests.AgentResponse;

namespace OpenAgent.Core.Runtime.Agent;

public sealed class AgentExecutor
{
    private const string DefaultAgentId = "default";

    private readonly IAgentRuntimeResolver _runtime;
    private readonly AgentFactory _agents;
    private readonly ConversationAgentResolver _conversationAgents;
    private readonly FileAssetRequestResolver _files;

    internal AgentExecutor(
        IAgentRuntimeResolver runtime,
        AgentFactory agents,
        ConversationAgentResolver conversationAgents,
        FileAssetRequestResolver files)
    {
        _runtime = runtime;
        _agents = agents;
        _conversationAgents = conversationAgents;
        _files = files;
    }

    public async Task<PlatformAgentResponse> ExecuteAsync(
        AgentRequest request,
        IAgentUserContext user,
        CancellationToken cancellationToken)
    {
        EnsureRequest(request);
        request = await _files.ResolveAsync(request, user, cancellationToken).ConfigureAwait(false);
        string traceId = ResolveTraceId(request.TraceId);
        string agentId = await ResolveAgentIdAsync(
            request,
            user,
            cancellationToken).ConfigureAwait(false);
        AgentRuntimeProfile profile = await _runtime.ResolveAsync(
            agentId,
            user,
            cancellationToken).ConfigureAwait(false);
        AgentRequest executionRequest = CopyWithResolvedValues(request, agentId, traceId);

        await using AgentExecutionScope scope = await _agents.CreateAsync(
            profile,
            executionRequest,
            user,
            cancellationToken).ConfigureAwait(false);
        AgentSession session = await scope.Agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        ChatMessage userMessage = AgentMessageAdapter.CreateUser(
            executionRequest.Query,
            executionRequest.Attachments);
        Microsoft.Agents.AI.AgentResponse response = await scope.Agent.RunAsync(
            userMessage,
            session,
            options: null,
            cancellationToken).ConfigureAwait(false);
        return new PlatformAgentResponse
        {
            Content = response.Text ?? string.Empty,
            TokenUsage = AgentResponseAdapter.ConvertUsage(response.Usage),
            TraceId = traceId,
            Success = true
        };
    }

    public async IAsyncEnumerable<AgentStreamEvent> ExecuteStreamingAsync(
        AgentRequest request,
        IAgentUserContext user,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        EnsureRequest(request);
        request = await _files.ResolveAsync(request, user, cancellationToken).ConfigureAwait(false);
        string traceId = ResolveTraceId(request.TraceId);
        string agentId = await ResolveAgentIdAsync(
            request,
            user,
            cancellationToken).ConfigureAwait(false);
        AgentRuntimeProfile profile = await _runtime.ResolveAsync(
            agentId,
            user,
            cancellationToken).ConfigureAwait(false);
        AgentRequest executionRequest = CopyWithResolvedValues(request, agentId, traceId);

        await using AgentExecutionScope scope = await _agents.CreateAsync(
            profile,
            executionRequest,
            user,
            cancellationToken).ConfigureAwait(false);
        AgentSession session = await scope.Agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        ChatMessage userMessage = AgentMessageAdapter.CreateUser(
            executionRequest.Query,
            executionRequest.Attachments);
        HashSet<string> announcedToolCalls = new(StringComparer.Ordinal);
        TokenUsage? usage = null;
        IAsyncEnumerable<AgentResponseUpdate> updates = scope.Agent.RunStreamingAsync(
            userMessage,
            session,
            options: null,
            cancellationToken);
        await foreach (AgentResponseUpdate update in updates.WithCancellation(cancellationToken))
        {
            IList<AIContent> contents = update.Contents ?? [];
            foreach (FunctionCallContent call in contents.OfType<FunctionCallContent>())
            {
                if (call.Exception != null || string.IsNullOrWhiteSpace(call.Name))
                {
                    continue;
                }

                string key = string.IsNullOrWhiteSpace(call.CallId) ? call.Name : call.CallId;
                if (announcedToolCalls.Add(key))
                {
                    yield return new AgentStreamEvent
                    {
                        Type = AgentStreamEventType.ToolCall,
                        ToolName = call.Name,
                        ToolCallId = call.CallId
                    };
                }
            }

            foreach (TextReasoningContent reasoning in contents.OfType<TextReasoningContent>())
            {
                if (!string.IsNullOrEmpty(reasoning.Text))
                {
                    yield return new AgentStreamEvent
                    {
                        Type = AgentStreamEventType.Reasoning,
                        Content = reasoning.Text
                    };
                }
            }

            string content = string.Concat(contents.OfType<TextContent>().Select(item => item.Text));
            if (!string.IsNullOrEmpty(content))
            {
                scope.AppendPartial(content);
                yield return new AgentStreamEvent
                {
                    Type = AgentStreamEventType.Content,
                    Content = content
                };
            }

            usage = AgentResponseAdapter.ReadUsage(contents) ?? usage;
        }

        if (usage != null)
        {
            yield return new AgentStreamEvent
            {
                Type = AgentStreamEventType.Usage,
                Usage = usage
            };
        }
    }

    private static void EnsureRequest(AgentRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
        {
            throw new AgentException(
                AgentErrorCode.MissingRequiredField,
                "Query is required");
        }
    }

    private async Task<string> ResolveAgentIdAsync(
        AgentRequest request,
        IAgentUserContext user,
        CancellationToken cancellationToken)
    {
        string? resolvedAgentId = await _conversationAgents.ResolveAsync(
            request,
            user,
            cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(resolvedAgentId)
            ? DefaultAgentId
            : resolvedAgentId;
    }

    private static string ResolveTraceId(string? traceId) =>
        string.IsNullOrWhiteSpace(traceId) ? Guid.NewGuid().ToString("N") : traceId;

    private static AgentRequest CopyWithResolvedValues(
        AgentRequest request,
        string agentId,
        string traceId) => new()
        {
            Query = request.Query,
            AgentId = agentId,
            ConversationId = request.ConversationId,
            TraceId = traceId,
            ClientType = request.ClientType,
            IdempotencyKey = request.IdempotencyKey,
            ExternalContext = request.ExternalContext,
            FileIds = request.FileIds,
            Attachments = request.Attachments
        };
}
