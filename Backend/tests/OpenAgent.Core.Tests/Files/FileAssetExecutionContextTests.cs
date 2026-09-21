using OpenAgent.Contracts.Runtime;
using OpenAgent.Core.Files;
using OpenAgent.Core.Tests.TestDoubles;
using Xunit;

namespace OpenAgent.Core.Tests.Files;

public class FileAssetExecutionContextTests
{
    [Fact]
    public void Scope_BeforeSet_IsNull()
    {
        var context = new FileAssetExecutionContext();

        Assert.Null(context.Scope);
        Assert.Null(context.Turn);
    }

    [Fact]
    public void Set_StoresTurnAndProjectsScope()
    {
        var context = new FileAssetExecutionContext();
        TurnContext turn = TurnContexts.Create("tenant-a", "user-a", "conversation-a");

        context.Set(turn);

        Assert.Equal(turn, context.Turn);
        Assert.Equal("tenant-a", context.Scope?.TenantId);
        Assert.Equal("user-a", context.Scope?.UserId);
        Assert.Equal("conversation-a", context.Scope?.ConversationId);
    }

    [Fact]
    public void Set_EqualTurnTwice_IsIdempotent()
    {
        var context = new FileAssetExecutionContext();
        context.Set(TurnContexts.Create());

        TurnContext equalTurn = TurnContexts.Create();

        context.Set(equalTurn);
        Assert.Equal(equalTurn, context.Turn);
    }

    [Fact]
    public void Set_DifferentTurn_Throws()
    {
        var context = new FileAssetExecutionContext();
        context.Set(TurnContexts.Create(traceId: "trace-first"));

        Assert.Throws<InvalidOperationException>(
            () => context.Set(TurnContexts.Create(traceId: "trace-second")));
    }
}
