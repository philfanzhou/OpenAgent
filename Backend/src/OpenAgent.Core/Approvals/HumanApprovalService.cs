using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Approvals;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Capabilities;
using OpenAgent.Core.Runtime.Agent;

namespace OpenAgent.Core.Approvals;

internal sealed class HumanApprovalService : IHumanApprovalService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHumanApprovalStore _approvals;
    private readonly IConversationStore _conversations;
    private readonly IAgentRuntimeResolver _runtime;
    private readonly AgentFactory _agents;
    private readonly HumanApprovalOptions _options;
    private readonly TimeProvider _timeProvider;

    public HumanApprovalService(
        IHumanApprovalStore approvals,
        IConversationStore conversations,
        IAgentRuntimeResolver runtime,
        AgentFactory agents,
        IOptions<HumanApprovalOptions> options,
        TimeProvider timeProvider)
    {
        _approvals = approvals;
        _conversations = conversations;
        _runtime = runtime;
        _agents = agents;
        _options = options.Value;
        _timeProvider = timeProvider;
    }

    internal async Task<HumanApprovalRequest> SuspendAsync(
        AgentExecutionScope scope,
        AgentSession session,
        ToolApprovalRequestContent approval,
        AgentRequest request,
        IAgentUserContext requester,
        CancellationToken cancellationToken)
    {
        string tenantId = requester.TenantId
            ?? throw new TenantDataIsolationException(
                null,
                null,
                "TenantId is required but not provided");
        if (string.IsNullOrWhiteSpace(request.ConversationId)
            || string.IsNullOrWhiteSpace(request.AgentId))
        {
            throw new AgentException(
                AgentErrorCode.InvalidRequest,
                "Approval requires a persisted conversation and resolved Agent id");
        }

        FunctionCallContent call = approval.ToolCall as FunctionCallContent
            ?? throw new InvalidOperationException(
                "Only local function tool approvals can be persisted and resumed.");
        ApprovalTarget target = scope.ApprovalTargets.ResolveRequired(approval);
        DateTimeOffset createdAt = _timeProvider.GetUtcNow();
        JsonElement sessionState = await scope.Agent.SerializeSessionAsync(
            session,
            jsonSerializerOptions: null,
            cancellationToken).ConfigureAwait(false);
        HumanApprovalRecord record = new()
        {
            ApprovalId = Guid.NewGuid().ToString("N"),
            TenantId = tenantId,
            ConversationId = request.ConversationId,
            AgentId = request.AgentId,
            TraceId = request.TraceId ?? string.Empty,
            Action = target.Action,
            TargetType = target.ResourceType,
            TargetCapability = target.ResourceId,
            RedactedArgumentsJson = ApprovalArgumentRedactor.SerializeRedacted(call.Arguments),
            RequestedBy = requester.UserId,
            CreatedAt = createdAt,
            ExpiresAt = createdAt.AddMinutes(Math.Max(1, _options.RequestTimeoutMinutes)),
            Status = HumanApprovalStatus.Preparing,
            MafRequestId = approval.RequestId,
            ToolCallId = call.CallId,
            ToolName = call.Name,
            SessionStateJson = sessionState.GetRawText(),
            RequesterContextJson = SerializeRequester(requester)
        };

        if (!await _approvals.CreateAsync(record, cancellationToken).ConfigureAwait(false))
        {
            throw new AgentException(
                AgentErrorCode.Conflict,
                "Approval request could not be persisted");
        }

        try
        {
            await scope.PauseAsync(
                record.ApprovalId,
                approval,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await _approvals.TryTransitionAsync(
                tenantId,
                record.ApprovalId,
                HumanApprovalStatus.Preparing,
                HumanApprovalStatus.Withdrawn,
                "system",
                "Conversation pause persistence failed.",
                _timeProvider.GetUtcNow(),
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        // The conversation pause is already durable. Finish activation even if the
        // originating HTTP/SSE request disconnects at this boundary.
        HumanApprovalRecord? ready = await _approvals.TryTransitionAsync(
            tenantId,
            record.ApprovalId,
            HumanApprovalStatus.Preparing,
            HumanApprovalStatus.Pending,
            "system",
            reason: null,
            _timeProvider.GetUtcNow(),
            CancellationToken.None).ConfigureAwait(false);
        if (ready == null)
        {
            await CancelConversationAsync(record, CancellationToken.None).ConfigureAwait(false);
            throw new AgentException(
                AgentErrorCode.Conflict,
                "Approval request could not be activated");
        }

        return ready.ToRequest();
    }

    public async Task<HumanApprovalRequest?> GetAsync(
        string tenantId,
        string approvalId,
        IAgentUserContext user,
        CancellationToken cancellationToken = default)
    {
        HumanApprovalAuthorizer.EnsureCanDecide(user);
        EnsureTenant(user, tenantId);
        await ExpirePendingAsync(tenantId, cancellationToken).ConfigureAwait(false);
        HumanApprovalRecord? record = await _approvals.GetAsync(
            tenantId,
            approvalId,
            cancellationToken).ConfigureAwait(false);
        return record?.ToRequest();
    }

    public async Task<IReadOnlyList<HumanApprovalRequest>> ListPendingAsync(
        string tenantId,
        IAgentUserContext user,
        CancellationToken cancellationToken = default)
    {
        HumanApprovalAuthorizer.EnsureCanDecide(user);
        EnsureTenant(user, tenantId);
        await ExpirePendingAsync(tenantId, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<HumanApprovalRecord> records = await _approvals.ListAsync(
            tenantId,
            HumanApprovalStatus.Pending,
            cancellationToken).ConfigureAwait(false);
        return records.Select(record => record.ToRequest()).ToList().AsReadOnly();
    }

    public async Task<HumanApprovalDecisionResult> DecideAsync(
        string tenantId,
        string approvalId,
        HumanApprovalDecisionRequest decision,
        IAgentUserContext approver,
        CancellationToken cancellationToken = default)
    {
        HumanApprovalAuthorizer.EnsureCanDecide(approver);
        EnsureTenant(approver, tenantId);
        HumanApprovalRecord record = await GetPendingAsync(
            tenantId,
            approvalId,
            cancellationToken).ConfigureAwait(false);
        DateTimeOffset decidedAt = _timeProvider.GetUtcNow();
        HumanApprovalStatus newStatus = decision.Approved
            ? HumanApprovalStatus.Approved
            : HumanApprovalStatus.Rejected;
        HumanApprovalRecord? transitioned = await _approvals.TryTransitionAsync(
            tenantId,
            approvalId,
            HumanApprovalStatus.Pending,
            newStatus,
            approver.UserId,
            decision.Reason,
            decidedAt,
            cancellationToken).ConfigureAwait(false);
        if (transitioned == null)
        {
            HumanApprovalRecord? current = await _approvals.GetAsync(
                tenantId,
                approvalId,
                cancellationToken).ConfigureAwait(false);
            if (current?.Status == HumanApprovalStatus.Pending
                && current.ExpiresAt <= decidedAt)
            {
                await ExpireRecordAsync(current, cancellationToken).ConfigureAwait(false);
                throw new AgentException(
                    AgentErrorCode.HumanApprovalTimeout,
                    "Approval request has expired");
            }
            throw new AgentException(
                AgentErrorCode.Conflict,
                "Approval request has already been decided");
        }

        if (!decision.Approved)
        {
            await CancelConversationAsync(transitioned, cancellationToken).ConfigureAwait(false);
            return new HumanApprovalDecisionResult
            {
                Approval = transitioned.ToRequest()
            };
        }

        try
        {
            // The approval transition is already durable. A disconnected
            // approver must not abort the authorized tool execution midway.
            await ResumeApprovedAsync(
                transitioned,
                CancellationToken.None).ConfigureAwait(false);
            return new HumanApprovalDecisionResult
            {
                Approval = transitioned.ToRequest()
            };
        }
        catch
        {
            await CancelConversationAsync(transitioned, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<HumanApprovalRequest> WithdrawAsync(
        string tenantId,
        string approvalId,
        IAgentUserContext requester,
        CancellationToken cancellationToken = default)
    {
        EnsureTenant(requester, tenantId);
        HumanApprovalRecord record = await GetPendingAsync(
            tenantId,
            approvalId,
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(record.RequestedBy, requester.UserId, StringComparison.Ordinal))
        {
            throw new AgentException(
                AgentErrorCode.PermissionDenied,
                "Only the approval requester can withdraw this request");
        }

        HumanApprovalRecord? withdrawn = await _approvals.TryTransitionAsync(
            tenantId,
            approvalId,
            HumanApprovalStatus.Pending,
            HumanApprovalStatus.Withdrawn,
            requester.UserId,
            "Approval request withdrawn.",
            _timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);
        if (withdrawn == null)
        {
            throw new AgentException(
                AgentErrorCode.Conflict,
                "Approval request has already been decided");
        }

        await CancelConversationAsync(withdrawn, cancellationToken).ConfigureAwait(false);
        return withdrawn.ToRequest();
    }

    public async Task<int> ExpirePendingAsync(
        CancellationToken cancellationToken = default) =>
        await ExpirePendingAsync(tenantId: null, cancellationToken).ConfigureAwait(false);

    private async Task<int> ExpirePendingAsync(
        string? tenantId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<HumanApprovalRecord> expired = await _approvals.ExpirePendingAsync(
            _timeProvider.GetUtcNow(),
            tenantId,
            cancellationToken).ConfigureAwait(false);
        foreach (HumanApprovalRecord record in expired)
        {
            await CancelConversationAsync(record, cancellationToken).ConfigureAwait(false);
        }
        return expired.Count;
    }

    private async Task<HumanApprovalRecord> GetPendingAsync(
        string tenantId,
        string approvalId,
        CancellationToken cancellationToken)
    {
        HumanApprovalRecord? record = await _approvals.GetAsync(
            tenantId,
            approvalId,
            cancellationToken).ConfigureAwait(false);
        if (record == null)
        {
            throw new AgentException(
                AgentErrorCode.Conflict,
                "Approval request is unavailable");
        }
        if (record.Status != HumanApprovalStatus.Pending)
        {
            throw new AgentException(
                AgentErrorCode.Conflict,
                "Approval request has already been decided");
        }
        if (record.ExpiresAt <= _timeProvider.GetUtcNow())
        {
            await ExpireRecordAsync(record, cancellationToken).ConfigureAwait(false);
            throw new AgentException(
                AgentErrorCode.HumanApprovalTimeout,
                "Approval request has expired");
        }
        return record;
    }

    private async Task ExpireRecordAsync(
        HumanApprovalRecord record,
        CancellationToken cancellationToken)
    {
        HumanApprovalRecord? expired = await _approvals.TryTransitionAsync(
            record.TenantId,
            record.ApprovalId,
            HumanApprovalStatus.Pending,
            HumanApprovalStatus.Expired,
            "system",
            "Approval request expired.",
            _timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);
        if (expired != null)
        {
            await CancelConversationAsync(expired, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ResumeApprovedAsync(
        HumanApprovalRecord approval,
        CancellationToken cancellationToken)
    {
        AgentUserContext requester = DeserializeRequester(approval);
        AgentRuntimeProfile profile = await _runtime.ResolveAsync(
            approval.AgentId,
            requester,
            cancellationToken).ConfigureAwait(false);
        ConversationRecord conversation = await _conversations.GetRecordAsync(
            approval.TenantId,
            approval.ConversationId,
            cancellationToken).ConfigureAwait(false)
            ?? throw new AgentException(
                AgentErrorCode.Conflict,
                "Approval conversation is unavailable");
        AgentRequest executionRequest = new()
        {
            Query = string.Empty,
            AgentId = approval.AgentId,
            ConversationId = approval.ConversationId,
            ConversationType = conversation.Type,
            TraceId = approval.TraceId
        };

        await using AgentExecutionScope scope = await _agents.CreateForResumeAsync(
            profile,
            executionRequest,
            requester,
            cancellationToken).ConfigureAwait(false);
        using JsonDocument sessionDocument = JsonDocument.Parse(approval.SessionStateJson);
        AgentSession session = await scope.Agent.DeserializeSessionAsync(
            sessionDocument.RootElement,
            jsonSerializerOptions: null,
            cancellationToken).ConfigureAwait(false);
        Dictionary<string, object?> arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(
            approval.RedactedArgumentsJson,
            JsonOptions) ?? [];
        FunctionCallContent call = new(
            approval.ToolCallId,
            approval.ToolName,
            arguments);
        ToolApprovalRequestContent requestContent = new(
            approval.MafRequestId,
            call);
        EnsureTargetUnchanged(scope, requestContent, approval);
        ChatMessage responseMessage = new(
            ChatRole.User,
            [requestContent.CreateResponse(
                approved: true,
                approval.DecisionReason)]);
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
            await SuspendAsync(
                scope,
                session,
                nextApproval,
                executionRequest,
                requester,
                cancellationToken).ConfigureAwait(false);
        }

    }

    private async Task CancelConversationAsync(
        HumanApprovalRecord approval,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            ConversationRecord? conversation = await _conversations.GetRecordAsync(
                approval.TenantId,
                approval.ConversationId,
                cancellationToken).ConfigureAwait(false);
            if (conversation == null
                || conversation.Status is ConversationStatus.Cancelled
                    or ConversationStatus.Completed
                    or ConversationStatus.Failed)
            {
                return;
            }

            if (await _conversations.UpdateStatusAsync(
                approval.TenantId,
                approval.ConversationId,
                ConversationStatus.Cancelled,
                conversation.Version,
                cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }

        throw new InvalidOperationException("Approval conversation could not be cancelled safely.");
    }

    private static void EnsureTargetUnchanged(
        AgentExecutionScope scope,
        ToolApprovalRequestContent request,
        HumanApprovalRecord approval)
    {
        ApprovalTarget current;
        try
        {
            current = scope.ApprovalTargets.ResolveRequired(request);
        }
        catch (InvalidOperationException exception)
        {
            throw new AgentException(
                AgentErrorCode.Conflict,
                "The approved target is no longer configured for human approval.",
                innerException: exception);
        }

        if (current.ResourceType != approval.TargetType
            || !string.Equals(current.ResourceId, approval.TargetCapability, StringComparison.Ordinal)
            || !string.Equals(current.Action, approval.Action, StringComparison.Ordinal))
        {
            throw new AgentException(
                AgentErrorCode.Conflict,
                "The approved target changed before execution.");
        }
    }

    private static string SerializeRequester(IAgentUserContext requester) =>
        JsonSerializer.Serialize(new RequesterContextSnapshot
        {
            UserId = requester.UserId,
            TenantId = requester.TenantId,
            Groups = [.. requester.Groups],
            Roles = [.. requester.Roles],
            Claims = new Dictionary<string, string>(requester.Claims, StringComparer.OrdinalIgnoreCase),
            Audience = [.. requester.Audience],
            IsAuthenticated = requester.IsAuthenticated
        }, JsonOptions);

    private static void EnsureTenant(IAgentUserContext user, string tenantId)
    {
        if (string.IsNullOrWhiteSpace(user.TenantId)
            || !string.Equals(user.TenantId, tenantId, StringComparison.Ordinal))
        {
            throw new TenantDataIsolationException(
                user.TenantId,
                tenantId,
                "Approval access cannot cross tenant boundaries.");
        }
    }

    private static AgentUserContext DeserializeRequester(HumanApprovalRecord approval)
    {
        RequesterContextSnapshot snapshot = JsonSerializer.Deserialize<RequesterContextSnapshot>(
            approval.RequesterContextJson,
            JsonOptions) ?? throw new InvalidOperationException(
                "Approval requester context is invalid.");
        if (!string.Equals(snapshot.UserId, approval.RequestedBy, StringComparison.Ordinal)
            || !string.Equals(snapshot.TenantId, approval.TenantId, StringComparison.Ordinal))
        {
            throw new TenantDataIsolationException(
                approval.TenantId,
                snapshot.TenantId,
                "Approval requester context does not match the persisted tenant and user.");
        }
        return new AgentUserContext
        {
            UserId = snapshot.UserId,
            TenantId = snapshot.TenantId,
            Groups = snapshot.Groups.AsReadOnly(),
            Roles = snapshot.Roles.AsReadOnly(),
            Claims = snapshot.Claims.AsReadOnly(),
            Audience = snapshot.Audience.AsReadOnly(),
            IsAuthenticated = snapshot.IsAuthenticated
        };
    }

    private sealed class RequesterContextSnapshot
    {
        public string UserId { get; init; } = string.Empty;
        public string? TenantId { get; init; }
        public List<string> Groups { get; init; } = [];
        public List<string> Roles { get; init; } = [];
        public Dictionary<string, string> Claims { get; init; } = new();
        public List<string> Audience { get; init; } = [];
        public bool IsAuthenticated { get; init; }
    }
}
