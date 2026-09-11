using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAgent.Contracts.Approvals;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Runtime.Agent;

namespace OpenAgent.Core.Approvals;

internal sealed class HumanApprovalService : IHumanApprovalService
{
    private readonly InMemoryHumanApprovalStore _store;
    private readonly IAgentRuntimeResolver _runtime;
    private readonly AgentFactory _agents;
    private readonly IConversationStore _conversations;

    public HumanApprovalService(
        InMemoryHumanApprovalStore store,
        IAgentRuntimeResolver runtime,
        AgentFactory agents,
        IConversationStore conversations)
    {
        _store = store;
        _runtime = runtime;
        _agents = agents;
        _conversations = conversations;
    }

    internal async Task<HumanApprovalRequest> SuspendAsync(
        AgentExecutionScope scope,
        AgentSession session,
        ToolApprovalRequestContent content,
        AgentRequest request,
        IAgentUserContext requester,
        CancellationToken cancellationToken)
    {
        if (content.ToolCall is not FunctionCallContent call
            || !string.Equals(call.Name, "execute_code", StringComparison.Ordinal))
        {
            throw new AgentException(
                AgentErrorCode.InvalidRequest,
                "Only execute_code can request human approval.");
        }

        string tenantId = requester.TenantId
            ?? throw new TenantDataIsolationException(message: "TenantId is required for approval.");
        string conversationId = request.ConversationId
            ?? throw new AgentException(AgentErrorCode.InvalidRequest, "Approval requires a conversation.");
        string agentId = request.AgentId
            ?? throw new AgentException(AgentErrorCode.InvalidRequest, "Approval requires a resolved agent.");
        JsonElement sessionState = await scope.Agent.SerializeSessionAsync(
            session,
            jsonSerializerOptions: null,
            cancellationToken).ConfigureAwait(false);
        HumanApprovalRequest approval = new()
        {
            ApprovalId = Guid.NewGuid().ToString("N"),
            TenantId = tenantId,
            ConversationId = conversationId,
            Action = call.Name,
            RedactedArgumentsJson = ApprovalArgumentRedactor.Serialize(call.Arguments),
            RequestedBy = requester.UserId,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15)
        };
        AgentUserContext requesterSnapshot = new()
        {
            UserId = requester.UserId,
            Username = requester.Username,
            Email = requester.Email,
            TenantId = requester.TenantId,
            Roles = requester.Roles,
            Groups = requester.Groups,
            Claims = requester.Claims,
            Audience = requester.Audience,
            IsAuthenticated = requester.IsAuthenticated
        };
        PendingApproval pending = new(
            approval,
            content,
            sessionState.GetRawText(),
            request,
            requesterSnapshot);
        if (!_store.Add(pending))
        {
            throw new AgentException(AgentErrorCode.Conflict, "Approval request could not be created.");
        }

        try
        {
            await scope.PauseAsync(approval.ApprovalId, content, cancellationToken).ConfigureAwait(false);
            return approval;
        }
        catch
        {
            _store.TryTake(tenantId, approval.ApprovalId, DateTimeOffset.UtcNow, out _);
            throw;
        }
    }

    public async Task<HumanApprovalDecisionResult> DecideAsync(
        string tenantId,
        string approvalId,
        HumanApprovalDecisionRequest decision,
        IAgentUserContext approver,
        CancellationToken cancellationToken = default)
    {
        EnsureTenant(tenantId, approver);
        if (!_store.TryTake(tenantId, approvalId, DateTimeOffset.UtcNow, out PendingApproval? pending)
            || pending == null)
        {
            throw new AgentException(AgentErrorCode.Conflict, "Approval request is unavailable or expired.");
        }

        if (!decision.Approved)
        {
            await CancelConversationAsync(pending.Request, cancellationToken).ConfigureAwait(false);
            return new HumanApprovalDecisionResult
            {
                Approval = pending.Request with { Status = HumanApprovalStatus.Rejected }
            };
        }

        try
        {
            AgentRuntimeProfile profile = await _runtime.ResolveAsync(
                pending.AgentRequest.AgentId!,
                pending.AgentRequest.LlmProfileId!,
                pending.Requester,
                cancellationToken).ConfigureAwait(false);
            await using AgentExecutionScope scope = await _agents.CreateForResumeAsync(
                profile,
                pending.AgentRequest,
                pending.Requester,
                cancellationToken).ConfigureAwait(false);
            using JsonDocument sessionDocument = JsonDocument.Parse(pending.SessionStateJson);
            AgentSession session = await scope.Agent.DeserializeSessionAsync(
                sessionDocument.RootElement,
                jsonSerializerOptions: null,
                cancellationToken).ConfigureAwait(false);
            ChatMessage responseMessage = new(
                ChatRole.User,
                [pending.Content.CreateResponse(decision.Approved, decision.Reason)]);
            Microsoft.Agents.AI.AgentResponse response = await scope.Agent.RunAsync(
                responseMessage,
                session,
                options: null,
                cancellationToken).ConfigureAwait(false);
            TokenUsage? usage = AgentResponseAdapter.ConvertUsage(response.Usage);
            string modelId = AgentResponseAdapter.ReadModelId(
                response.RawRepresentation,
                profile.Model.ModelId);
            ToolApprovalRequestContent? nextApproval = response.Messages
                .SelectMany(message => message.Contents)
                .OfType<ToolApprovalRequestContent>()
                .FirstOrDefault();
            if (nextApproval == null)
            {
                await scope.CompleteAsync(usage, modelId, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                HumanApprovalRequest next = await SuspendAsync(
                    scope,
                    session,
                    nextApproval,
                    pending.AgentRequest,
                    pending.Requester,
                    cancellationToken).ConfigureAwait(false);
                return new HumanApprovalDecisionResult
                {
                    Approval = pending.Request with { Status = HumanApprovalStatus.Approved },
                    NextApproval = next
                };
            }
        }
        catch
        {
            await CancelConversationAsync(pending.Request, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        return new HumanApprovalDecisionResult
        {
            Approval = pending.Request with { Status = HumanApprovalStatus.Approved }
        };
    }

    private async Task CancelConversationAsync(
        HumanApprovalRequest approval,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            ConversationRecord? current = await _conversations.GetRecordAsync(
                approval.TenantId,
                approval.ConversationId,
                cancellationToken).ConfigureAwait(false);
            if (current == null || current.Status != ConversationStatus.AwaitingApproval)
            {
                return;
            }

            if (await _conversations.UpdateStatusAsync(
                approval.TenantId,
                approval.ConversationId,
                ConversationStatus.Cancelled,
                current.Version,
                cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }

        throw new AgentException(AgentErrorCode.Conflict, "Approval conversation changed while deciding.");
    }

    private static void EnsureTenant(string tenantId, IAgentUserContext user)
    {
        if (string.IsNullOrWhiteSpace(user.TenantId)
            || !string.Equals(user.TenantId, tenantId, StringComparison.Ordinal))
        {
            throw new TenantDataIsolationException(user.TenantId, tenantId);
        }
    }
}
