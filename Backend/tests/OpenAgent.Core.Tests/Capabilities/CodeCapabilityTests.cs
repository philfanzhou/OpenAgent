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
    public async Task Invoke_ReadsAuthorizedInputBeforeRunnerCall()
    {
        var fixture = new Fixture();
        FileAsset input = await fixture.Files.UploadAsync(new FileAssetCreateRequest
        {
            FileName = "data.csv", MediaType = "text/csv", Source = FileAssetSource.UserUpload
        }, new MemoryStream("quantity\n42\n"u8.ToArray()), fixture.Context.Scope!, CancellationToken.None);
        await fixture.Files.EnsureReferencesAsync([input.FileId], fixture.Context.Scope!, CancellationToken.None);
        AIFunction function = await fixture.GetFunctionAsync();
        object? result = await function.InvokeAsync(new AIFunctionArguments
        {
            ["code"] = "print('read input')",
            ["inputFiles"] = new[] { new { fileId = input.FileId, name = "data.csv" } }
        });
        Assert.Contains("exitCode", result?.ToString());
        ExecutionFile file = Assert.Single(Assert.Single(fixture.Executor.Requests).Files);
        Assert.Equal("data.csv", file.Name);
        Assert.Equal("quantity\n42\n"u8.ToArray(), file.Content);
        Assert.Equal(1, fixture.Objects.ReadCount);
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

    [RunnerIntegrationFact]
    public async Task MafLoop_RealRunnerGeneratesEditsAndPublishesAuthorizedArtifact()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(180) };
        var executor = new RunnerClient(http, Options.Create(new CodeExecutionOptions
        {
            Enabled = true,
            Endpoint = Environment.GetEnvironmentVariable("CODEACT_TEST_RUNNER_ENDPOINT") ?? string.Empty,
            ApiKey = Environment.GetEnvironmentVariable("CODEACT_TEST_RUNNER_KEY") ?? string.Empty
        }));
        var fixture = new Fixture(executor: executor);
        FileAsset input = await fixture.Files.UploadAsync(new FileAssetCreateRequest
        {
            FileName = "sales.csv", MediaType = "text/csv", Source = FileAssetSource.UserUpload
        }, new MemoryStream("region,quantity\nEast,42\n"u8.ToArray()), fixture.Context.Scope!, CancellationToken.None);
        await fixture.Files.EnsureReferencesAsync([input.FileId], fixture.Context.Scope!, CancellationToken.None);
        AIFunction function = await fixture.GetFunctionAsync();
        string code = """
            import csv, os
            from openpyxl import Workbook
            assert os.getuid() == 65532
            assert 'Runner__ApiKey' not in os.environ
            with open('/input/sales.csv') as source:
                row = next(csv.DictReader(source))
            book = Workbook()
            book.active['A1'] = row['region']
            book.active['B1'] = int(row['quantity'])
            book.save('/output/report.xlsx')
            print('generated 42')
            """;
        var provider = new SequenceChatProvider([
            [new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("bad", "execute_code", new Dictionary<string, object?> { ["code"] = "misspelled()" })])],
            [new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("generate", "execute_code", new Dictionary<string, object?>
            {
                ["code"] = code, ["inputFiles"] = new[] { new { fileId = input.FileId, name = "sales.csv" } }
            })])],
            [new ChatResponseUpdate(ChatRole.Assistant, "Generated the workbook.")]
        ]);
        var agent = new ChatClientAgent(provider, new ChatClientAgentOptions { ChatOptions = new() { Tools = [function] } });
        await foreach (AgentResponseUpdate _ in agent.RunStreamingAsync("Read the CSV and create an Excel workbook.")) { }
        Assert.Contains(provider.Requests[1].SelectMany(message => message.Contents).OfType<FunctionResultContent>(),
            result => result.CallId == "bad" && result.Result?.ToString()?.Contains("NameError", StringComparison.Ordinal) == true);
        FunctionResultContent generated = Assert.Single(provider.Requests[2].SelectMany(message => message.Contents)
            .OfType<FunctionResultContent>(), result => result.CallId == "generate");
        using JsonDocument generation = JsonDocument.Parse(generated.Result!.ToString()!);
        Assert.True(generation.RootElement.TryGetProperty("exitCode", out _), generation.RootElement.ToString());
        Assert.Equal(0, generation.RootElement.GetProperty("exitCode").GetInt32());
        string fileId = generation.RootElement.GetProperty("files")[0].GetProperty("fileId").GetString()!;
        FileAsset asset = fixture.Repository.Assets[fileId];
        Assert.Equal("tenant", asset.TenantId);
        Assert.Equal("user", asset.OwnerUserId);
        Assert.Contains("conversation:" + fileId, fixture.Repository.References);

        object? edited = await function.InvokeAsync(new AIFunctionArguments
        {
            ["inputFiles"] = new[] { new { fileId, name = "report.xlsx" } },
            ["code"] = "from openpyxl import load_workbook\nw=load_workbook('/input/report.xlsx')\nassert w.active['B1'].value == 42\nw.active['B1']=84\nw.save('/output/updated.xlsx')\nassert load_workbook('/output/updated.xlsx').active['B1'].value == 84\nprint('verified 84')"
        });
        using JsonDocument edit = JsonDocument.Parse(edited!.ToString()!);
        Assert.True(edit.RootElement.TryGetProperty("exitCode", out _), edit.RootElement.ToString());
        Assert.Equal(0, edit.RootElement.GetProperty("exitCode").GetInt32());
        string editedId = edit.RootElement.GetProperty("files")[0].GetProperty("fileId").GetString()!;
        IReadOnlyList<AITool> tools = await fixture.Factory.CreateAsync("agent", new AgentConfig
        {
            CodeExecution = new() { Enabled = true }
        }, fixture.User, CancellationToken.None);
        AIFunction publish = Assert.IsAssignableFrom<AIFunction>(Assert.Single(tools, tool => tool.Name == "publish_files"));
        await publish.InvokeAsync(new AIFunctionArguments { ["fileIds"] = new[] { editedId } });
        Assert.Equal(editedId, Assert.Single(fixture.Context.Published).FileId);
        FileAssetContent content = await fixture.Files.ReadAsync(editedId, fixture.Context.Scope!, CancellationToken.None);
        using var archive = new System.IO.Compression.ZipArchive(new MemoryStream(content.Data));
        Assert.NotNull(archive.GetEntry("xl/workbook.xml"));
        using var sheet = new StreamReader(archive.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        Assert.Contains("<v>84</v>", await sheet.ReadToEndAsync());
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

        internal Fixture(bool enabled = true, ICodeExecutor? executor = null)
        {
            Files = new FileAssetService(Repository, Objects, Options.Create(new FileAssetOptions { Enabled = true }));
            Context.Set(new FileAssetScope { TenantId = "tenant", UserId = "user", ConversationId = "conversation" });
            var auth = new Mock<IAgentAuthorizationService>();
            auth.Setup(service => service.IsAuthorizedAsync(It.IsAny<AgentAuthorizationRequest>(), It.IsAny<IAgentUserContext>(), It.IsAny<CancellationToken>()))
                .Returns(() => Task.FromResult(Authorized));
            var gate = new AgentAuthorizationGate(auth.Object);
            var source = new CodeCapabilitySource(executor ?? Executor, Files, Context, gate,
                Options.Create(new CodeExecutionOptions { Enabled = enabled }));
            var sources = new List<ICapabilitySource> { source };
            if (executor != null)
            {
                var fileOptions = Options.Create(new FileAssetOptions { Enabled = true });
                sources.Add(new FileAssetCapabilitySource(Files, Context, fileOptions,
                    new FileAssetUrlDownloader(Mock.Of<IHttpClientFactory>(), fileOptions)));
            }
            Factory = new CapabilityToolFactory(sources, gate);
        }

        internal async Task<AIFunction> GetFunctionAsync() => Assert.IsAssignableFrom<AIFunction>(Assert.Single(
            await Factory.CreateAsync("agent", new AgentConfig { CodeExecution = new() { Enabled = true } }, User, CancellationToken.None),
            tool => tool.Name == "execute_code"));
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

internal sealed class RunnerIntegrationFactAttribute : FactAttribute
{
    public RunnerIntegrationFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("RUN_CODEACT_RUNNER_TESTS") != "1")
        {
            Skip = "Set RUN_CODEACT_RUNNER_TESTS=1 and CODEACT_TEST_RUNNER_ENDPOINT/KEY to test an installed Runner.";
        }
    }
}
