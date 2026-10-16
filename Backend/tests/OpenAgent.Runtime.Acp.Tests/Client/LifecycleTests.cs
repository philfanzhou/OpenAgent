using System.Text.Json;
using OpenAgent.Runtime.Acp.Agent;
using OpenAgent.Runtime.Acp.Client;
using OpenAgent.Runtime.Acp.Protocol;
using OpenAgent.Runtime.Acp.Rpc;
using OpenAgent.Runtime.Acp.Tests.TestDoubles;
using Xunit;
namespace OpenAgent.Runtime.Acp.Tests.Client;
public sealed class LifecycleTests
{
    private static AcpAgentHandlers Handlers => new()
    {
        Initialize = (_, _) => Task.FromResult(new InitializeResponse()),
        NewSession = (_, _, _) => Task.FromResult(new SessionResponse(Guid.NewGuid().ToString())),
        Prompt = (_, _, _) => Task.FromResult(new PromptResponse("end_turn"))
    };
    [Fact]
    public async Task NewSession_EarlyUpdatesAreRetainedBeforeBinding()
    {
        var (a, b) = DuplexTransport.Pair();
        await using AcpAgentHost host = new(b, Handlers with { NewSession = async (_, ctx, ct) => { await ctx.UpdateAsync("early", new AgentMessageChunk(new TextContent("早到🥐")), ct); return new("early"); } });
        await using AcpClient client = new(a); await client.InitializeAsync(); await using AcpSession session = await client.NewSessionAsync(new(Path.GetTempPath(), []));
        await using var reader = session.ReadUpdatesAsync().GetAsyncEnumerator(); Assert.True(await reader.MoveNextAsync());
        Assert.Equal("早到🥐", Assert.IsType<TextContent>(Assert.IsType<AgentMessageChunk>(reader.Current.Update).Content).Text);
    }
    [Fact]
    public async Task DisposeSession_PreservesAllQueuedTailUpdatesForConsumer()
    {
        var (a, b) = DuplexTransport.Pair();
        await using AcpAgentHost host = new(b, Handlers with { Prompt = async (r, ctx, ct) => { for (int i = 0; i < 100; i++) await ctx.UpdateAsync(r.SessionId, new AgentMessageChunk(new TextContent(i.ToString())), ct); return new("end_turn"); } });
        await using AcpClient client = new(a); await client.InitializeAsync(); await using AcpSession session = await client.NewSessionAsync(new(Path.GetTempPath(), []));
        await using AcpPromptTurn turn = await session.StartPromptAsync([new TextContent("burst")]); await turn.Completion;
        await session.DisposeAsync();
        int count = 0; await foreach (SessionNotification item in session.ReadUpdatesAsync()) count++;
        Assert.Equal(100, count);
    }
    [Fact]
    public async Task AgentSessionQuota_ReservesWhileHandlerIsStillRunning()
    {
        var (a, b) = DuplexTransport.Pair(); TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously), finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using AcpAgentHost host = new(b, Handlers with { NewSession = async (_, _, ct) => { entered.TrySetResult(); await finish.Task.WaitAsync(ct); return new("one"); } }, new() { MaxSessions = 1 });
        await using AcpClient client = new(a); await client.InitializeAsync(); Task<AcpSession> first = client.NewSessionAsync(new(Path.GetTempPath(), [])); await entered.Task;
        AcpRpcException denied = await Assert.ThrowsAsync<AcpRpcException>(() => client.NewSessionAsync(new(Path.GetTempPath(), []))); Assert.Equal(10002, denied.Code);
        finish.TrySetResult(); await using AcpSession session = await first;
    }
    [Fact]
    public async Task ClientSessionQuota_RejectsBeforeSendingAnotherNewSession()
    {
        var (a, b) = DuplexTransport.Pair(); int calls = 0;
        await using AcpAgentHost host = new(b, Handlers with { NewSession = (_, _, _) => { Interlocked.Increment(ref calls); return Task.FromResult(new SessionResponse("one")); } });
        await using AcpClient client = new(a, options: new() { MaxSessions = 1 }); await client.InitializeAsync(); await using AcpSession session = await client.NewSessionAsync(new(Path.GetTempPath(), []));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.NewSessionAsync(new(Path.GetTempPath(), []))); Assert.Equal(1, calls);
    }
    [Fact]
    public async Task Permission_DefaultDenialAndInvalidChoiceAreDistinct()
    {
        foreach (bool invalid in new[] { false, true })
        {
            var (a, b) = DuplexTransport.Pair();
            await using AcpAgentHost host = new(b, Handlers with { Prompt = async (r, ctx, ct) => { PermissionResponse result = await ctx.RequestPermissionAsync(new(r.SessionId, new("t"), [new("allow", "Allow", "allow_once")]), ct); Assert.Equal("cancelled", result.Outcome.Outcome); return new("end_turn"); } });
            await using AcpClient client = new(a, invalid ? new() { Permission = (_, _) => Task.FromResult(PermissionResponse.Selected("nonexistent")) } : null);
            await client.InitializeAsync(); await using AcpSession session = await client.NewSessionAsync(new(Path.GetTempPath(), [])); await using AcpPromptTurn turn = await session.StartPromptAsync([new TextContent("permission")]);
            if (invalid) Assert.Equal(-32602, (await Assert.ThrowsAsync<AcpRpcException>(() => turn.Completion)).Code);
            else Assert.Equal("end_turn", (await turn.Completion).StopReason);
        }
    }
    [Fact]
    public async Task PromptCancellation_CancelsPendingPermissionAndAllowsReuse()
    {
        var (a, b) = DuplexTransport.Pair(); TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously), cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using AcpAgentHost host = new(b, Handlers with { Prompt = async (r, ctx, ct) => { await ctx.RequestPermissionAsync(new(r.SessionId, new("t"), []), ct); ct.ThrowIfCancellationRequested(); return new("end_turn"); } });
        await using AcpClient client = new(a, new() { Permission = async (_, ct) => { entered.TrySetResult(); try { await Task.Delay(Timeout.Infinite, ct); } finally { cancelled.TrySetResult(); } return PermissionResponse.Cancelled; } });
        await client.InitializeAsync(); await using AcpSession session = await client.NewSessionAsync(new(Path.GetTempPath(), [])); await using AcpPromptTurn turn = await session.StartPromptAsync([new TextContent("wait")]);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); await turn.CancelAndWaitAsync(); Assert.Equal("cancelled", (await turn.Completion).StopReason); await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
    [Fact]
    public async Task Prompt_UnconfirmedCancelClosesConnectionAndReportsFailure()
    {
        var (a, b) = DuplexTransport.Pair(); TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously); TaskCompletionSource<PromptResponse> never = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using AcpAgentHost host = new(b, Handlers with { Prompt = (_, _, _) => { entered.TrySetResult(); return never.Task; } }, new() { ShutdownTimeout = TimeSpan.FromMilliseconds(20) });
        await using AcpClient client = new(a, options: new() { CancelGracePeriod = TimeSpan.FromMilliseconds(30), ShutdownTimeout = TimeSpan.FromMilliseconds(20) });
        await client.InitializeAsync(); await using AcpSession session = await client.NewSessionAsync(new(Path.GetTempPath(), [])); await using AcpPromptTurn turn = await session.StartPromptAsync([new TextContent("wait")]);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<AcpCancellationUnconfirmedException>(() => turn.CancelAndWaitAsync()); Assert.True(client.Connection.Completion.IsCompleted);
        await Assert.ThrowsAnyAsync<IOException>(() => turn.Completion); never.TrySetResult(new("end_turn"));
    }
    [Fact]
    public async Task SlowConsumer_ClosesWithBufferFailureInsteadOfDroppingUpdates()
    {
        var (a, b) = DuplexTransport.Pair();
        await using AcpAgentHost host = new(b, Handlers with { Prompt = async (r, ctx, ct) => { for (int i = 0; i < 20; i++) await ctx.UpdateAsync(r.SessionId, new AgentMessageChunk(new TextContent("x")), ct); return new("end_turn"); } });
        await using AcpClient client = new(a, options: new() { MaxQueuedUpdates = 1 }); await client.InitializeAsync(); await using AcpSession session = await client.NewSessionAsync(new(Path.GetTempPath(), [])); await using AcpPromptTurn turn = await session.StartPromptAsync([new TextContent("burst")]);
        await Assert.ThrowsAnyAsync<IOException>(() => turn.Completion); Assert.Contains("buffer", client.Connection.Failure!.Message);
    }
    [Theory]
    [InlineData("{\"name\":\"ok\"}", true)]
    [InlineData("{}", false)]
    [InlineData("{\"name\":42}", false)]
    [InlineData("{\"name\":\"ok\",\"unknown\":true}", false)]
    public async Task Elicitation_AcceptValidatesRequiredFieldsAndTypes(string content, bool valid)
    {
        var (a, b) = DuplexTransport.Pair();
        await using AcpAgentHost host = new(b, Handlers with { Prompt = async (r, ctx, ct) => { await ctx.ElicitAsync(new("Name", "form") { SessionId = r.SessionId, RequestedSchema = AcpJson.Parse("""{"type":"object","properties":{"name":{"type":"string"}},"required":["name"]}""") }, ct); return new("end_turn"); } });
        await using AcpClient client = new(a, new() { FormElicitation = true, Elicitation = (_, _) => Task.FromResult(new ElicitationResponse("accept", AcpJson.Parse(content))) });
        await client.InitializeAsync(); await using AcpSession session = await client.NewSessionAsync(new(Path.GetTempPath(), [])); await using AcpPromptTurn turn = await session.StartPromptAsync([new TextContent("form")]);
        if (valid) Assert.Equal("end_turn", (await turn.Completion).StopReason); else Assert.Equal(-32602, (await Assert.ThrowsAsync<AcpRpcException>(() => turn.Completion)).Code);
    }
}
