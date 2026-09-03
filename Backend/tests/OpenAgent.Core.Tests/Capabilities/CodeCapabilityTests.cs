using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Moq;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Execution;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Capabilities;
using OpenAgent.Core.Capabilities.Code;
using OpenAgent.Core.Files;
using OpenAgent.Core.Security;
using OpenAgent.Core.Tests.TestDoubles;
using Xunit;

namespace OpenAgent.Core.Tests.Capabilities;

public class CodeCapabilityTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Discover_RequiresBothHostAndAgentOptIn(bool hostEnabled, bool agentEnabled)
    {
        var fixture = new Fixture(hostEnabled);
        IReadOnlyList<AITool> tools = await fixture.Factory.CreateAsync("agent", new AgentConfig
        {
            CodeExecution = new() { Enabled = agentEnabled }
        }, fixture.User, CancellationToken.None);
        Assert.Empty(tools);
    }

    [Theory]
    [InlineData("", "user", "conversation")]
    [InlineData("tenant", "", "conversation")]
    [InlineData("tenant", "user", "")]
    public async Task Discover_RequiresIdentifiedConversation(string tenant, string user, string conversation)
    {
        var fixture = new Fixture();
        fixture.Context.Set(new FileAssetScope { TenantId = tenant, UserId = user, ConversationId = conversation });
        Assert.Empty(await fixture.Factory.CreateAsync("agent", new AgentConfig
        {
            CodeExecution = new() { Enabled = true }
        }, fixture.User, CancellationToken.None));
    }

    [Fact]
    public async Task Invoke_StopsExecutingWhenRequestBudgetIsExhausted()
    {
        var fixture = new Fixture();
        AIFunction function = await fixture.GetFunctionAsync();
        for (int index = 0; index < 8; index++)
        {
            await function.InvokeAsync(new AIFunctionArguments { ["code"] = "print(1)" });
        }
        object? result = await function.InvokeAsync(new AIFunctionArguments { ["code"] = "print(2)" });
        Assert.Contains("budget exhausted", result?.ToString());
        Assert.Equal(8, fixture.Executor.Requests.Count);
    }

    [Theory]
    [InlineData("other-tenant", "user")]
    [InlineData("tenant", "other-user")]
    public async Task Invoke_RejectsForeignFilesBeforeRunnerCall(string tenant, string owner)
    {
        var fixture = new Fixture();
        FileAsset foreign = await fixture.Files.UploadAsync(new FileAssetCreateRequest
        {
            FileName = "private.txt", MediaType = "text/plain", Source = FileAssetSource.UserUpload
        }, new MemoryStream("private"u8.ToArray()), new FileAssetScope { TenantId = tenant, UserId = owner, ConversationId = "other" }, CancellationToken.None);
        fixture.Repository.References.Add("conversation:" + foreign.FileId);
        AIFunction function = await fixture.GetFunctionAsync();
        object? result = await function.InvokeAsync(new AIFunctionArguments
        {
            ["code"] = "print('test')",
            ["inputFiles"] = new[] { new { fileId = foreign.FileId, name = "data.txt" } }
        });
        Assert.Contains("unavailable", result?.ToString());
        Assert.Empty(fixture.Executor.Requests);
        Assert.Equal(0, fixture.Objects.ReadCount);
    }

    [Fact]
    public async Task Invoke_RechecksAuthorizationAfterDiscovery()
    {
        var fixture = new Fixture();
        AIFunction function = await fixture.GetFunctionAsync();
        fixture.Authorized = false;
        await Assert.ThrowsAsync<AgentException>(async () => await function.InvokeAsync(new AIFunctionArguments { ["code"] = "print(1)" }));
        Assert.Empty(fixture.Executor.Requests);
    }

    [Fact]
    public async Task Invoke_RegistersBinaryArtifactsWithConversationOwnership()
    {
        var fixture = new Fixture();
        fixture.Executor.Results.Enqueue(new CodeExecutionResult
        {
            ExecutionId = "run-1",
            Files = [new ExecutionFile { Name = "report.xlsx", Content = [80, 75, 3, 4, 1, 2, 3] }]
        });
        AIFunction function = await fixture.GetFunctionAsync();
        object? result = await function.InvokeAsync(new AIFunctionArguments { ["code"] = "generate_report()" });
        FileAsset artifact = Assert.Single(fixture.Repository.Assets.Values);
        Assert.Equal("tenant", artifact.TenantId);
        Assert.Equal("user", artifact.OwnerUserId);
        Assert.Equal(FileAssetState.Ready, artifact.State);
        Assert.Contains("conversation:" + artifact.FileId, fixture.Repository.References);
        Assert.Contains(artifact.FileId, result?.ToString());
        Assert.DoesNotContain("UEs", result?.ToString());
        Assert.Equal(new byte[] { 80, 75, 3, 4, 1, 2, 3 }, fixture.Objects.LastContent);
    }

    [Fact]
    public async Task MafLoop_ReceivesExecutionErrorAndExecutesCorrectedCode()
    {
        var fixture = new Fixture();
        fixture.Executor.Results.Enqueue(new CodeExecutionResult { ExitCode = 1, Stderr = "NameError: misspelled" });
        fixture.Executor.Results.Enqueue(new CodeExecutionResult { ExitCode = 0, Stdout = "42" });
        AIFunction function = await fixture.GetFunctionAsync();
        var provider = new SequenceChatProvider([
            [new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("code-1", "execute_code", new Dictionary<string, object?> { ["code"] = "misspelled()" })])],
            [new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("code-2", "execute_code", new Dictionary<string, object?> { ["code"] = "print(6*7)" })])],
            [new ChatResponseUpdate(ChatRole.Assistant, "42")]
        ]);
        var agent = new ChatClientAgent(provider, new ChatClientAgentOptions { ChatOptions = new() { Tools = [function] } });
        await foreach (AgentResponseUpdate _ in agent.RunStreamingAsync("Calculate using code")) { }
        Assert.Equal(2, fixture.Executor.Requests.Count);
        Assert.Equal("print(6*7)", fixture.Executor.Requests[1].Code);
        Assert.Contains(provider.Requests[1].SelectMany(message => message.Contents).OfType<FunctionResultContent>(),
            result => result.Result?.ToString()?.Contains("NameError", StringComparison.Ordinal) == true);
        Assert.Contains(provider.Requests[2].SelectMany(message => message.Contents).OfType<FunctionResultContent>(),
            result => result.CallId == "code-2" && result.Result?.ToString()?.Contains("42", StringComparison.Ordinal) == true);
    }

    private sealed class Fixture
    {
        internal bool Authorized { get; set; } = true;
        internal RecordingFileAssetRepository Repository { get; } = new();
        internal RecordingFileObjectStore Objects { get; } = new();
        internal FakeExecutor Executor { get; } = new();
        internal FileAssetExecutionContext Context { get; } = new();
        internal AgentUserContext User { get; } = new() { TenantId = "tenant", UserId = "user" };
        internal FileAssetService Files { get; }
        internal CapabilityToolFactory Factory { get; }

        internal Fixture(bool enabled = true)
        {
            Files = new FileAssetService(Repository, Objects, Options.Create(new FileAssetOptions { Enabled = true }));
            Context.Set(new FileAssetScope { TenantId = "tenant", UserId = "user", ConversationId = "conversation" });
            var auth = new Mock<IAgentAuthorizationService>();
            auth.Setup(service => service.IsAuthorizedAsync(It.IsAny<AgentAuthorizationRequest>(), It.IsAny<IAgentUserContext>(), It.IsAny<CancellationToken>()))
                .Returns(() => Task.FromResult(Authorized));
            var gate = new AgentAuthorizationGate(auth.Object);
            var source = new CodeCapabilitySource(Executor, Files, Context, gate,
                Options.Create(new CodeExecutionOptions { Enabled = enabled }));
            Factory = new CapabilityToolFactory([source], gate);
        }

        internal async Task<AIFunction> GetFunctionAsync() => Assert.IsAssignableFrom<AIFunction>(Assert.Single(
            await Factory.CreateAsync("agent", new AgentConfig { CodeExecution = new() { Enabled = true } }, User, CancellationToken.None)));
    }

    private sealed class FakeExecutor : ICodeExecutor
    {
        internal List<CodeExecutionRequest> Requests { get; } = [];
        internal Queue<CodeExecutionResult> Results { get; } = new();
        public Task<CodeExecutionResult> ExecuteAsync(CodeExecutionRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(Results.TryDequeue(out CodeExecutionResult? result) ? result : new CodeExecutionResult());
        }
    }
}
