using System.Text.Json;
using OpenAgent.Contracts.Approvals;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Approvals;
using Xunit;

namespace OpenAgent.Core.Tests.Approvals;

public sealed class HumanApprovalStateTests
{
    [Fact]
    public void SerializeRedacted_RemovesNestedSecretsButKeepsUsefulArguments()
    {
        string json = ApprovalArgumentRedactor.SerializeRedacted(
            new Dictionary<string, object?>
            {
                ["repository"] = "openagent",
                ["credentials"] = new Dictionary<string, object?>
                {
                    ["api-key"] = "secret-value"
                }
            });

        Assert.Contains("openagent", json, StringComparison.Ordinal);
        Assert.Contains("***", json, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Store_PersistsApprovalAndEnforcesTenantIsolation()
    {
        var store = new InMemoryHumanApprovalStore();
        HumanApprovalRecord approval = CreateApproval("approval-persisted");

        Assert.True(await store.CreateAsync(approval));

        HumanApprovalRecord stored = Assert.IsType<HumanApprovalRecord>(
            await store.GetAsync("tenant-a", approval.ApprovalId));
        Assert.Equal("conversation-1", stored.ConversationId);
        Assert.Equal("agent-1", stored.AgentId);
        Assert.Equal("deploy", stored.Action);
        Assert.Equal(AgentResourceType.Function, stored.TargetType);
        Assert.Equal("dangerous_function", stored.TargetCapability);
        Assert.Equal("requester-1", stored.RequestedBy);
        Assert.Null(await store.GetAsync("tenant-b", approval.ApprovalId));
    }

    [Fact]
    public async Task Store_ExpirationTransitionsOnlyExpiredPendingRequests()
    {
        var store = new InMemoryHumanApprovalStore();
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-20T12:00:00Z");
        await store.CreateAsync(CreateApproval("expired", now.AddMinutes(-1)));
        await store.CreateAsync(CreateApproval("active", now.AddMinutes(1)));

        IReadOnlyList<HumanApprovalRecord> expired = await store.ExpirePendingAsync(now);

        HumanApprovalRecord item = Assert.Single(expired);
        Assert.Equal("expired", item.ApprovalId);
        Assert.Equal(HumanApprovalStatus.Expired, item.Status);
        Assert.Equal(
            HumanApprovalStatus.Pending,
            (await store.GetAsync("tenant-a", "active"))?.Status);
    }

    [Fact]
    public async Task Store_ConcurrentDecisions_OnlyOneTransitionWins()
    {
        var store = new InMemoryHumanApprovalStore();
        HumanApprovalRecord approval = CreateApproval("approval-concurrent");
        await store.CreateAsync(approval);
        DateTimeOffset decidedAt = approval.CreatedAt.AddSeconds(1);

        Task<HumanApprovalRecord?>[] attempts = Enumerable.Range(0, 16)
            .Select(index => store.TryTransitionAsync(
                "tenant-a",
                approval.ApprovalId,
                HumanApprovalStatus.Pending,
                HumanApprovalStatus.Approved,
                $"approver-{index}",
                "approved",
                decidedAt))
            .ToArray();
        HumanApprovalRecord?[] results = await Task.WhenAll(attempts);

        Assert.Single(results, result => result != null);
        Assert.Equal(
            HumanApprovalStatus.Approved,
            (await store.GetAsync("tenant-a", approval.ApprovalId))?.Status);
    }

    [Fact]
    public void Authorizer_RequiresIndependentApprovalPermission()
    {
        var requester = new AgentUserContext
        {
            UserId = "requester",
            TenantId = "tenant-a",
            IsAuthenticated = true
        };
        var approver = new AgentUserContext
        {
            UserId = "approver",
            TenantId = "tenant-a",
            Roles = ["ApprovalApprover"],
            IsAuthenticated = true
        };

        Assert.False(HumanApprovalAuthorizer.CanDecide(requester));
        Assert.True(HumanApprovalAuthorizer.CanDecide(approver));
    }

    [Fact]
    public void ApprovalRecord_SerializationExposesOnlyRedactedArguments()
    {
        HumanApprovalRecord approval = CreateApproval("approval-public-shape");

        string json = JsonSerializer.Serialize(approval);

        Assert.Contains("RedactedArgumentsJson", json, StringComparison.Ordinal);
        Assert.DoesNotContain("SessionStateJson", json, StringComparison.Ordinal);
        Assert.DoesNotContain("RequesterContextJson", json, StringComparison.Ordinal);
    }

    private static HumanApprovalRecord CreateApproval(
        string approvalId,
        DateTimeOffset? expiresAt = null)
    {
        DateTimeOffset createdAt = DateTimeOffset.Parse("2026-08-20T10:00:00Z");
        return new HumanApprovalRecord
        {
            ApprovalId = approvalId,
            TenantId = "tenant-a",
            ConversationId = "conversation-1",
            AgentId = "agent-1",
            TraceId = "trace-1",
            Action = "deploy",
            TargetType = AgentResourceType.Function,
            TargetCapability = "dangerous_function",
            RedactedArgumentsJson = "{\"password\":\"***\"}",
            RequestedBy = "requester-1",
            CreatedAt = createdAt,
            ExpiresAt = expiresAt ?? createdAt.AddHours(1),
            Status = HumanApprovalStatus.Pending,
            MafRequestId = "maf-request-1",
            ToolCallId = "tool-call-1",
            ToolName = "dangerous_function",
            SessionStateJson = "{}",
            RequesterContextJson = "{}"
        };
    }
}
