using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Runtime;
using Xunit;

namespace OpenAgent.Contracts.Tests;

/// <summary>
/// The audit-plane projection of a turn. Its keys are a contract with
/// OpenAgent.Audit's masking gateway (see docs/openagent-integration.md), so
/// renaming one silently breaks session grouping on the audit side.
/// </summary>
public class TurnContextAuditAttributeTests
{
    private static TurnContext Turn() => new()
    {
        TenantId = "tenant-1",
        UserId = "usr_1",
        ConversationId = "conv-1",
        TraceId = "trace-1",
        AgentId = "cs-bot",
        ConversationType = ConversationType.User
    };

    [Fact]
    public void ToAuditAttributes_EmitsTheGatewayContractKeys()
    {
        IReadOnlyDictionary<string, string?> attributes = Turn().ToAuditAttributes();

        Assert.Equal("conv-1", attributes[AuditAttributeNames.ConversationId]);
        Assert.Equal("usr_1", attributes[AuditAttributeNames.UserId]);
        Assert.Equal("cs-bot", attributes[AuditAttributeNames.AgentId]);
        Assert.Equal("tenant-1", attributes[AuditAttributeNames.TenantId]);
        Assert.Equal("trace-1", attributes[AuditAttributeNames.TraceId]);
    }

    [Fact]
    public void AuditAttributeNames_MatchTheDocumentedGatewayDefaults()
    {
        // These literals mirror DEFAULT_SESSION_ATTRS / DEFAULT_USER_ATTRS /
        // DEFAULT_AGENT_ATTRS in openagent_audit/config.py.
        Assert.Equal("openagent.conversation_id", AuditAttributeNames.ConversationId);
        Assert.Equal("openagent.user_id", AuditAttributeNames.UserId);
        Assert.Equal("openagent.agent_id", AuditAttributeNames.AgentId);
    }

    [Fact]
    public void ToAuditAttributes_OmitsBlankTenantButKeepsEmptyOptionalIds()
    {
        var turn = new TurnContext
        {
            TenantId = "  ",
            UserId = "usr_1",
            ConversationId = null,
            TraceId = "trace-1",
            AgentId = null
        };

        IReadOnlyDictionary<string, string?> attributes = turn.ToAuditAttributes();

        Assert.Null(attributes[AuditAttributeNames.TenantId]);
        Assert.Null(attributes[AuditAttributeNames.ConversationId]);
        Assert.Null(attributes[AuditAttributeNames.AgentId]);
        Assert.Equal("usr_1", attributes[AuditAttributeNames.UserId]);
    }
}