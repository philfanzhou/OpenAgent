using System.Text.Json;
using Microsoft.Extensions.Options;
using Moq;
using OpenAgent.Contracts.Execution;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Capabilities.Code;
using OpenAgent.Core.Capabilities.Skill;
using OpenAgent.Core.Files;
using OpenAgent.Core.Security;
using OpenAgent.Core.Tests.TestDoubles;
using Xunit;

namespace OpenAgent.Core.Tests.Capabilities;

public class SkillScriptRunnerTests : IAsyncLifetime
{
    private readonly Fixture _fixture = new();
    private readonly string _packageRoot = Path.Combine(Path.GetTempPath(), "openagent-skill-tests", Guid.NewGuid().ToString("N"));

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_packageRoot);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_packageRoot))
        {
            Directory.Delete(_packageRoot, recursive: true);
        }
        return Task.CompletedTask;
    }

    private async Task<string> WriteScriptAsync(string name, string content)
    {
        await File.WriteAllTextAsync(Path.Combine(_packageRoot, name), content);
        return Path.Combine(_packageRoot, name);
    }

    [Fact]
    public async Task RunAsync_HostOptionDisabled_ThrowsWithoutRunnerCall()
    {
        var fixture = new Fixture(enabled: false);
        string scriptPath = await WriteScriptAsync("analyze.py", "print('analyze ran')\n");
        await Assert.ThrowsAsync<AgentException>(
            () => fixture.RunAsync(scriptPath, null));
        Assert.Empty(fixture.Executor.Requests);
    }

    [Fact]
    public async Task RunAsync_Unauthorized_ThrowsPermissionDeniedWithoutRunnerCall()
    {
        _fixture.Authorized = false;
        string scriptPath = await WriteScriptAsync("analyze.py", "print('analyze ran')\n");
        AgentException exception = await Assert.ThrowsAsync<AgentException>(
            () => _fixture.RunAsync(scriptPath, null));
        Assert.Equal(AgentErrorCode.PermissionDenied, exception.ErrorCode);
        Assert.Empty(_fixture.Executor.Requests);
    }

    [Fact]
    public async Task RunAsync_MountsScriptAndSiblingsThroughWrapper()
    {
        string scriptPath = await WriteScriptAsync("analyze.py", "print('analyze ran')\n");
        await File.WriteAllTextAsync(Path.Combine(_packageRoot, "helper.py"), "SUPPORT = 42\n");
        await File.WriteAllTextAsync(Path.Combine(_packageRoot, "notes.txt"), "sibling data is mounted too\n");
        object? result = await _fixture.RunAsync(scriptPath, null);
        CodeExecutionRequest request = Assert.Single(_fixture.Executor.Requests);
        Assert.Contains("runpy.run_path(\"/input/analyze.py\"", request.Code, StringComparison.Ordinal);
        Assert.Equal(
            new[] { "analyze.py", "helper.py", "notes.txt" },
            request.Files.Select(file => file.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain(request.Files, file => file.Name.Equals("main.py", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("exitCode", result?.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_NullArguments_WrapperSkipsJsonLoads()
    {
        string scriptPath = await WriteScriptAsync("analyze.py", "print('analyze ran')\n");
        await _fixture.RunAsync(scriptPath, null);
        string code = Assert.Single(_fixture.Executor.Requests).Code;
        Assert.Contains("raw = None", code, StringComparison.Ordinal);
        Assert.DoesNotContain("json.loads", code, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_FlatStringArguments_JsonEncodedIntoWrapper()
    {
        string scriptPath = await WriteScriptAsync("analyze.py", "print('analyze ran')\n");
        using var document = JsonDocument.Parse("{\"count\":\"1\",\"label\":\"two\"}");
        JsonElement? arguments = document.RootElement;
        await _fixture.RunAsync(scriptPath, arguments);
        string code = Assert.Single(_fixture.Executor.Requests).Code;
        Assert.Contains("json.loads(", code, StringComparison.Ordinal);
        Assert.Contains("\\\"count\\\":\\\"1\\\"", code, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_JavaScriptScript_UsesJavaScriptWrapperAndLanguage()
    {
        string scriptPath = await WriteScriptAsync("analyze.js", "console.log('analyze ran')\n");
        await _fixture.RunAsync(scriptPath, null);
        CodeExecutionRequest request = Assert.Single(_fixture.Executor.Requests);
        Assert.Equal(ExecutionLanguage.JavaScript, request.Language);
        Assert.Contains("await import(pathToFileURL(\"/input/analyze.js\")", request.Code, StringComparison.Ordinal);
        Assert.Contains("const raw = null", request.Code, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_EsmScript_AlsoRunsAsJavaScript()
    {
        string scriptPath = await WriteScriptAsync("analyze.mjs", "console.log('esm ran')\n");
        await _fixture.RunAsync(scriptPath, null);
        CodeExecutionRequest request = Assert.Single(_fixture.Executor.Requests);
        Assert.Equal(ExecutionLanguage.JavaScript, request.Language);
        Assert.Contains("/input/analyze.mjs", request.Code, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_JavaScriptArguments_MappedOntoArgv()
    {
        string scriptPath = await WriteScriptAsync("analyze.js", "console.log('analyze ran')\n");
        using var document = JsonDocument.Parse("{\"count\":\"1\",\"label\":\"two\"}");
        JsonElement? arguments = document.RootElement;
        await _fixture.RunAsync(scriptPath, arguments);
        string code = Assert.Single(_fixture.Executor.Requests).Code;
        Assert.Contains("\"count\":\"1\"", code, StringComparison.Ordinal);
        Assert.Contains("Object.values(raw)", code, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_PythonScript_KeepsPythonLanguage()
    {
        string scriptPath = await WriteScriptAsync("analyze.py", "print('analyze ran')\n");
        await _fixture.RunAsync(scriptPath, null);
        CodeExecutionRequest request = Assert.Single(_fixture.Executor.Requests);
        Assert.Equal(ExecutionLanguage.Python, request.Language);
        Assert.Contains("runpy.run_path", request.Code, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ReservedMainMjsScript_RejectedWithoutRunnerCall()
    {
        string scriptPath = await WriteScriptAsync("main.mjs", "console.log('never runs')\n");
        object? result = await _fixture.RunAsync(scriptPath, null);
        Assert.Contains("reserved", result?.ToString(), StringComparison.Ordinal);
        Assert.Empty(_fixture.Executor.Requests);
    }

    [Fact]
    public async Task RunAsync_ReservedMainPyScript_RejectedWithoutRunnerCall()
    {
        string scriptPath = await WriteScriptAsync("main.py", "print('never runs')\n");
        object? result = await _fixture.RunAsync(scriptPath, null);
        Assert.Contains("reserved", result?.ToString(), StringComparison.Ordinal);
        Assert.Empty(_fixture.Executor.Requests);
    }

    [Fact]
    public async Task RunAsync_MissingScriptFile_ReturnsErrorWithoutRunnerCall()
    {
        string scriptPath = Path.Combine(_packageRoot, "gone.py");
        object? result = await _fixture.RunAsync(scriptPath, null);
        Assert.Contains("error", result?.ToString(), StringComparison.Ordinal);
        Assert.Empty(_fixture.Executor.Requests);
    }

    [Fact]
    public async Task RunAsync_ExhaustedSharedBudget_ReturnsErrorWithoutRunnerCall()
    {
        string scriptPath = await WriteScriptAsync("analyze.py", "print('analyze ran')\n");
        for (int index = 0; index < 8; index++)
        {
            Assert.True(_fixture.Budget.TryConsume(8));
        }
        object? result = await _fixture.RunAsync(scriptPath, null);
        Assert.Contains("budget exhausted", result?.ToString(), StringComparison.Ordinal);
        Assert.Empty(_fixture.Executor.Requests);
    }

    [Fact]
    public async Task RunAsync_RegistersArtifactsWithConversationOwnership()
    {
        string scriptPath = await WriteScriptAsync("analyze.py", "print('analyze ran')\n");
        _fixture.Executor.Results.Enqueue(new CodeExecutionResult
        {
            ExecutionId = "run-1",
            ExitCode = 0,
            Files = [new ExecutionFile { Name = "summary.txt", Content = "done"u8.ToArray() }]
        });
        object? result = await _fixture.RunAsync(scriptPath, null);
        FileAsset artifact = Assert.Single(_fixture.Repository.Assets.Values);
        Assert.Equal("tenant", artifact.TenantId);
        Assert.Equal("user", artifact.OwnerUserId);
        Assert.Contains("conversation:" + artifact.FileId, _fixture.Repository.References);
        Assert.Contains(artifact.FileId, result?.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("ZG9uZQ", result?.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_UnsupportedArtifactExtension_ReturnsError()
    {
        string scriptPath = await WriteScriptAsync("analyze.py", "print('analyze ran')\n");
        _fixture.Executor.Results.Enqueue(new CodeExecutionResult
        {
            ExecutionId = "run-1",
            ExitCode = 0,
            Files = [new ExecutionFile { Name = "payload.exe", Content = new byte[] { 1, 2, 3 } }]
        });
        object? result = await _fixture.RunAsync(scriptPath, null);
        Assert.Contains("Unsupported generated file type", result?.ToString(), StringComparison.Ordinal);
    }

    [RunnerIntegrationFact]
    public async Task RunAsync_RealRunnerExecutesScriptWithArgumentsAndPublishesArtifact()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(180) };
        var executor = new RunnerClient(http, Options.Create(new CodeExecutionOptions
        {
            Enabled = true,
            Endpoint = Environment.GetEnvironmentVariable("CODEACT_TEST_RUNNER_ENDPOINT") ?? string.Empty,
            ApiKey = Environment.GetEnvironmentVariable("CODEACT_TEST_RUNNER_KEY") ?? string.Empty
        }));
        Fixture fixture = new(executor: executor);
        string scriptPath = await WriteScriptAsync("verify.py", """
            import sys
            assert sys.argv[1:] == ["1", "two"], sys.argv
            with open("/output/skill-result.txt", "w") as output:
                output.write("ok")
            print("script completed")
            """);
        using var document = JsonDocument.Parse("{\"count\":\"1\",\"label\":\"two\"}");
        JsonElement? arguments = document.RootElement;
        object? result = await fixture.RunAsync(scriptPath, arguments);
        using JsonDocument parsed = JsonDocument.Parse(result!.ToString()!);
        Assert.True(parsed.RootElement.TryGetProperty("exitCode", out _), parsed.RootElement.ToString());
        Assert.Equal(0, parsed.RootElement.GetProperty("exitCode").GetInt32());
        Assert.Equal("script completed", parsed.RootElement.GetProperty("stdout").GetString());
        string fileId = parsed.RootElement.GetProperty("files")[0].GetProperty("fileId").GetString()!;
        FileAsset asset = fixture.Repository.Assets[fileId];
        Assert.Equal("tenant", asset.TenantId);
        Assert.Equal("user", asset.OwnerUserId);
    }

    private sealed class Fixture
    {
        internal bool Authorized { get; set; } = true;
        internal RecordingFileAssetRepository Repository { get; } = new();
        internal RecordingFileObjectStore Objects { get; } = new();
        internal FakeCodeExecutor Executor { get; } = new();
        internal CodeExecutionBudget Budget { get; } = new();
        internal FileAssetExecutionContext Context { get; } = new();
        internal AgentUserContext User { get; } = new() { TenantId = "tenant", UserId = "user" };

        internal Fixture(bool enabled = true, ICodeExecutor? executor = null)
        {
            Files = new FileAssetService(Repository, Objects, Options.Create(new FileAssetOptions { Enabled = true }));
            Context.Set(new FileAssetScope { TenantId = "tenant", UserId = "user", ConversationId = "conversation" });
            var auth = new Mock<IAgentAuthorizationService>();
            auth.Setup(service => service.IsAuthorizedAsync(
                    It.IsAny<AgentAuthorizationRequest>(), It.IsAny<IAgentUserContext>(), It.IsAny<CancellationToken>()))
                .Returns(() => Task.FromResult(Authorized));
            var gate = new AgentAuthorizationGate(auth.Object);
            Runner = new SkillScriptRunner(
                executor ?? Executor, Files, Context, gate, Budget,
                Options.Create(new CodeExecutionOptions { Enabled = enabled }));
        }

        internal FileAssetService Files { get; }
        internal SkillScriptRunner Runner { get; }

        internal Task<object?> RunAsync(string scriptPath, JsonElement? arguments) =>
            Runner.RunAsync("agent", User, "report-writer", scriptPath, arguments, CancellationToken.None);
    }
}
