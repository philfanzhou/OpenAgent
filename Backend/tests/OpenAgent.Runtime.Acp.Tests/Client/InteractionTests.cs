using OpenAgent.Runtime.Acp.Agent;
using OpenAgent.Runtime.Acp.Client;
using OpenAgent.Runtime.Acp.Protocol;
using OpenAgent.Runtime.Acp.Rpc;
using OpenAgent.Runtime.Acp.Tests.TestDoubles;
using Xunit;
namespace OpenAgent.Runtime.Acp.Tests.Client;
public sealed class InteractionTests
{
    private static AcpAgentHandlers Handlers => new()
    {
        Initialize = (_, _) => Task.FromResult(new InitializeResponse()),
        NewSession = (_, _, _) => Task.FromResult(new SessionResponse("test")),
        Prompt = (_, _, _) => Task.FromResult(new PromptResponse("end_turn"))
    };
    [Theory]
    [InlineData(0)]
    [InlineData(17)]
    public async Task TerminalAuthentication_UsesHostCallbackAndExitCodeWithoutAuthenticateRpc(int exit)
    {
        var (a, b) = DuplexTransport.Pair(); int calls = 0;
        await using AcpAgentHost host = new(b, Handlers with { Initialize = (r, ct) => { Assert.True(r.ClientCapabilities!.Auth!.Value.TryGetProperty("terminal", out _)); return Task.FromResult(new InitializeResponse(AuthMethods: [new("login", "Login", Type: "terminal") { Args = ["login"] }])); } });
        await using AcpClient client = new(a, new() { TerminalAuthentication = (method, _) => { calls++; Assert.Equal("login", method.Args![0]); return Task.FromResult(exit); } });
        await client.InitializeAsync();
        if (exit == 0) await client.AuthenticateAsync("login"); else Assert.Equal(-32000, (await Assert.ThrowsAsync<AcpRpcException>(() => client.AuthenticateAsync("login"))).Code);
        Assert.Equal(1, calls); Assert.Equal(0, client.Connection.PendingCount);
    }
    [Fact]
    public async Task UrlCompletion_OnlyKnownConnectionIdIsDeliveredOnce()
    {
        var (a, b) = DuplexTransport.Pair(); int completions = 0;
        await using AcpAgentHost host = new(b, Handlers with { Prompt = async (r, ctx, ct) =>
        {
            await ctx.ElicitationCompleteAsync("unknown", ct);
            await ctx.ElicitAsync(new("Consent", "url") { SessionId = r.SessionId, ElicitationId = "known", Url = "https://example.com" }, ct);
            await ctx.ElicitationCompleteAsync("known", ct); await ctx.ElicitationCompleteAsync("known", ct); return new("end_turn");
        } });
        await using AcpClient client = new(a, new() { UrlElicitation = true, Elicitation = (_, _) => Task.FromResult(new ElicitationResponse("accept")), ElicitationComplete = _ => Interlocked.Increment(ref completions) });
        await client.InitializeAsync(); await using AcpSession session = await client.NewSessionAsync(new(Path.GetTempPath(), [])); await using AcpPromptTurn turn = await session.StartPromptAsync([new TextContent("url")]); await turn.Completion;
        Assert.Equal(1, completions);
    }
}
