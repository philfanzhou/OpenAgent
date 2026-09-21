using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Execution;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using OpenAgent.Contracts.Skills;
using OpenAgent.Core.Capabilities.Code;
using OpenAgent.Core.Capabilities.Skill;
using OpenAgent.Core.Files;
using OpenAgent.Core.Security;
using OpenAgent.Core.Tests.TestDoubles;
using Xunit;
using Xunit.Abstractions;

namespace OpenAgent.Core.Tests.Capabilities;

public class AgentSkillsProviderFactoryTests
{
    private readonly ITestOutputHelper _output;

    public AgentSkillsProviderFactoryTests(ITestOutputHelper output) => _output = output;

    private const string SkillName = "report-writer";

    [Fact]
    public async Task MafLoop_RunSkillScript_ExecutesPackageScriptThroughIsolatedRunner()
    {
        var fixture = new FactoryFixture();
        await using AgentSkillsRuntime runtime = await fixture.CreateRuntimeAsync(agentCodeExecution: true, scriptExecutionEnabled: true);
        Assert.NotNull(runtime.Provider);

        var chat = new RecordingChatClient(
        [
            [new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("call-1", "run_skill_script",
                new Dictionary<string, object?> { ["skillName"] = SkillName, ["scriptName"] = "scripts/analyze.py" })])],
            [new ChatResponseUpdate(ChatRole.Assistant, "analysis complete")]
        ]);
        var agent = new ChatClientAgent(chat, new ChatClientAgentOptions
        {
            ChatOptions = new ChatOptions(),
            AIContextProviders = [runtime.Provider!]
        });
        await foreach (AgentResponseUpdate _ in agent.RunStreamingAsync("Run the analysis script.")) { }

        if (fixture.Executor.Requests.Count == 0)
        {
            FunctionResultContent? diagnostic = chat.Requests.Count > 1
                ? chat.Requests[1].SelectMany(message => message.Contents).OfType<FunctionResultContent>().FirstOrDefault()
                : null;
            _output.WriteLine($"function result: {diagnostic?.Result}");
        }
        CodeExecutionRequest request = Assert.Single(fixture.Executor.Requests);
        Assert.Contains("runpy.run_path(\"/input/scripts/analyze.py\"", request.Code, StringComparison.Ordinal);
        Assert.Contains("sys.path.insert(0, \"/input\")", request.Code, StringComparison.Ordinal);
        Assert.Contains(request.Files, file => file.Name.EndsWith("analyze.py", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("SKILL.md", request.Files.Select(file => file.Name), StringComparer.OrdinalIgnoreCase);
        FunctionResultContent result = Assert.Single(chat.Requests[1].SelectMany(message => message.Contents)
            .OfType<FunctionResultContent>(), content => content.CallId == "call-1");
        Assert.Contains("exitCode", result.Result?.ToString(), StringComparison.Ordinal);
    }
    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task MafLoop_WithoutDoubleOptIn_ScriptNeverReachesRunner(
        bool agentCodeExecution, bool scriptExecutionEnabled, bool hostCodeExecutionEnabled)
    {
        var fixture = new FactoryFixture(hostCodeExecutionEnabled);
        await using AgentSkillsRuntime runtime = await fixture.CreateRuntimeAsync(agentCodeExecution, scriptExecutionEnabled);
        Assert.NotNull(runtime.Provider);

        var chat = new RecordingChatClient(
        [
            [new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("call-1", "run_skill_script",
                new Dictionary<string, object?> { ["skillName"] = SkillName, ["scriptName"] = "scripts/analyze.py" })])],
            [new ChatResponseUpdate(ChatRole.Assistant, "done")]
        ]);
        var agent = new ChatClientAgent(chat, new ChatClientAgentOptions
        {
            ChatOptions = new ChatOptions(),
            AIContextProviders = [runtime.Provider!]
        });
        await foreach (AgentResponseUpdate _ in agent.RunStreamingAsync("Run the analysis script.")) { }

        Assert.Empty(fixture.Executor.Requests);
        Assert.Contains(chat.Requests[1].SelectMany(message => message.Contents), content => content is FunctionResultContent);
    }

    private sealed class FactoryFixture
    {
        internal RecordingFileAssetRepository Repository { get; } = new();
        internal RecordingFileObjectStore Objects { get; } = new();
        internal SkillCatalog Catalog { get; } = new();
        internal FakeCodeExecutor Executor { get; } = new();
        internal FileAssetExecutionContext Context { get; } = new();
        internal AgentUserContext User { get; } = new() { TenantId = "tenant", UserId = "user" };
        internal AgentSkillsProviderFactory Factory { get; }

        internal FactoryFixture(bool hostCodeExecutionEnabled = true)
        {
            var auth = new Mock<IAgentAuthorizationService>();
            auth.Setup(service => service.IsAuthorizedAsync(
                    It.IsAny<AgentAuthorizationRequest>(), It.IsAny<IAgentUserContext>(), It.IsAny<CancellationToken>()))
                .Returns(Task.FromResult(true));
            Factory = new AgentSkillsProviderFactory(
                Objects,
                Catalog,
                new AgentAuthorizationGate(auth.Object),
                Executor,
                new FileAssetService(Repository, Objects, Options.Create(new FileAssetOptions { Enabled = true })),
                Context,
                Options.Create(new CodeExecutionOptions { Enabled = hostCodeExecutionEnabled }),
                NullLoggerFactory.Instance);
        }

        internal async Task<AgentSkillsRuntime> CreateRuntimeAsync(bool agentCodeExecution, bool scriptExecutionEnabled)
        {
            Context.Set(TurnContexts.Create());
            RegisterPackage();
            return await Factory.CreateAsync(
                "agent",
                new AgentConfig
                {
                    CodeExecution = new CodeExecutionConfig { Enabled = agentCodeExecution },
                    Skills = new SkillsConfig
                    {
                        Instances = [NewInstance(scriptExecutionEnabled)]
                    }
                },
                User,
                CancellationToken.None);
        }

        private SkillInstanceConfig NewInstance(bool scriptExecutionEnabled) => new()
        {
            TenantId = "tenant",
            Id = "skill-1",
            Name = SkillName,
            Type = SkillTypes.AgentSkill,
            Enabled = true,
            ObjectKey = "files/tenants/" + FileObjectTenantScope.CreatePartition("tenant") + "/skill-packages/skill-1",
            PackageFormat = "directory",
            ScriptExecutionEnabled = scriptExecutionEnabled
        };

        private void RegisterPackage()
        {
            SkillInstanceConfig instance = NewInstance(true);
            byte[] skillMd = Encoding.UTF8.GetBytes($"""
                ---
                name: {SkillName}
                description: Writes reports from templates.
                ---

                # Report writer

                Run scripts/analyze.py for analysis.
                """);
            byte[] script = Encoding.UTF8.GetBytes("print('analyze ran')\n");
            string baseKey = instance.ObjectKey!;
            var index = new SkillPackageStorageIndex
            {
                TenantId = "tenant",
                Files =
                [
                    new SkillPackageStorageFile
                    {
                        RelativePath = "SKILL.md",
                        ObjectKey = baseKey + "/0",
                        Sha256 = Convert.ToHexString(SHA256.HashData(skillMd)).ToLowerInvariant()
                    },
                    new SkillPackageStorageFile
                    {
                        RelativePath = "scripts/analyze.py",
                        ObjectKey = baseKey + "/1",
                        Sha256 = Convert.ToHexString(SHA256.HashData(script)).ToLowerInvariant()
                    }
                ]
            };
            Objects.ContentsByKey[baseKey] = JsonSerializer.SerializeToUtf8Bytes(index);
            Objects.ContentsByKey[baseKey + "/0"] = skillMd;
            Objects.ContentsByKey[baseKey + "/1"] = script;
            Catalog.Register(instance);
        }
    }

    private sealed class RecordingChatClient(IReadOnlyList<IReadOnlyList<ChatResponseUpdate>> turns) : IChatClient
    {
        internal List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(messages.ToList());
            IReadOnlyList<ChatResponseUpdate> turn = turns[Math.Min(Requests.Count - 1, turns.Count - 1)];
            foreach (ChatResponseUpdate update in turn)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return update;
                await Task.Yield();
            }
        }

        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("RecordingChatClient only supports streaming responses.");

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceKey == null && serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose()
        {
        }
    }
}
