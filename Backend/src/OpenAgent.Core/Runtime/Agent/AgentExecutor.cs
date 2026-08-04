using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Security;
using PlatformAgentResponse = OpenAgent.Contracts.Requests.AgentResponse;

namespace OpenAgent.Core.Runtime.Agent;

public sealed class AgentExecutor
{
    private const string DefaultAgentId = "default";

    private readonly IAgentConfigProvider _configs;
    private readonly AgentAuthorizationGate _authorization;
    private readonly AgentFactory _agents;

    internal AgentExecutor(
        IAgentConfigProvider configs,
        AgentAuthorizationGate authorization,
        AgentFactory agents)
    {
        _configs = configs;
        _authorization = authorization;
        _agents = agents;
    }

    public async Task<PlatformAgentResponse> ExecuteAsync(
        AgentRequest request,
        IAgentUserContext user,
        CancellationToken cancellationToken)
    {
        EnsureRequest(request);
        string traceId = ResolveTraceId(request.TraceId);
        string agentId = ResolveAgentId(request.AgentId);
        AgentConfig config = await LoadConfigAsync(agentId, cancellationToken).ConfigureAwait(false);
        LlmConfig model = await _authorization.ResolveAuthorizedModelAsync(
            agentId,
            config.Llm,
            user,
            cancellationToken).ConfigureAwait(false);
        AgentRequest executionRequest = CopyWithResolvedValues(request, agentId, traceId);

        await using AgentLease lease = await _agents.CreateAsync(
            agentId,
            config,
            model,
            executionRequest,
            user,
            cancellationToken).ConfigureAwait(false);
        AgentSession session = await lease.Agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        ChatMessage userMessage = AgentMessageAdapter.CreateUser(
            executionRequest.Query,
            executionRequest.Attachments);
        Microsoft.Agents.AI.AgentResponse response = await lease.Agent.RunAsync(
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
        string traceId = ResolveTraceId(request.TraceId);
        string agentId = ResolveAgentId(request.AgentId);
        AgentConfig config = await LoadConfigAsync(agentId, cancellationToken).ConfigureAwait(false);
        LlmConfig model = await _authorization.ResolveAuthorizedModelAsync(
            agentId,
            config.Llm,
            user,
            cancellationToken).ConfigureAwait(false);
        AgentRequest executionRequest = CopyWithResolvedValues(request, agentId, traceId);

        await using AgentLease lease = await _agents.CreateAsync(
            agentId,
            config,
            model,
            executionRequest,
            user,
            cancellationToken).ConfigureAwait(false);
        AgentSession session = await lease.Agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        ChatMessage userMessage = AgentMessageAdapter.CreateUser(
            executionRequest.Query,
            executionRequest.Attachments);
        HashSet<string> announcedToolCalls = new(StringComparer.Ordinal);
        TokenUsage? usage = null;
        IAsyncEnumerable<AgentResponseUpdate> updates = lease.Agent.RunStreamingAsync(
            userMessage,
            session,
            options: null,
            cancellationToken);
        await foreach (AgentResponseUpdate update in updates.WithCancellation(cancellationToken))
        {
            foreach (FunctionCallContent call in update.Contents.OfType<FunctionCallContent>())
            {
                string key = string.IsNullOrEmpty(call.CallId) ? call.Name : call.CallId;
                if (!string.IsNullOrWhiteSpace(call.Name) && announcedToolCalls.Add(key))
                {
                    yield return new AgentStreamEvent
                    {
                        Type = AgentStreamEventType.ToolCall,
                        ToolName = call.Name,
                        ToolCallId = call.CallId
                    };
                }
            }

            foreach (TextReasoningContent reasoning in update.Contents.OfType<TextReasoningContent>())
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

            string content = update.Text ?? string.Empty;
            if (!string.IsNullOrEmpty(content))
            {
                lease.AppendPartial(content);
                yield return new AgentStreamEvent
                {
                    Type = AgentStreamEventType.Content,
                    Content = content
                };
            }

            usage = AgentResponseAdapter.ReadUsage(update) ?? usage;
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

    private async Task<AgentConfig> LoadConfigAsync(
        string agentId,
        CancellationToken cancellationToken)
    {
        return await _configs.GetConfigAsync(agentId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Agent configuration not found: {agentId}");
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

    private static string ResolveAgentId(string? agentId) =>
        string.IsNullOrWhiteSpace(agentId) ? DefaultAgentId : agentId;

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
            EnabledSkills = request.EnabledSkills,
            ContextPolicy = request.ContextPolicy,
            Attachments = request.Attachments
        };
}
