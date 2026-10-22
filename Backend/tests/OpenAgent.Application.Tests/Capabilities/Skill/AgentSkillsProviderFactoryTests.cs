using OpenAgent.Capabilities.Tests.TestDoubles;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Moq;
using OpenAgent.Contracts.Capabilities;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Execution;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Runtime;
using OpenAgent.Contracts.Security;
using OpenAgent.Contracts.Skills;
using OpenAgent.Application.Capabilities.Skill;
using OpenAgent.Capabilities.Files.Requests;
using OpenAgent.Capabilities.Files.Services;
using OpenAgent.Capabilities.Integrations.Runner;
using OpenAgent.Application.Tests.TestDoubles;
using OpenAgent.Capabilities.Security;
using Xunit;

namespace OpenAgent.Application.Tests.Capabilities;

public class AgentSkillsProviderFactoryTests
{
    private const string SkillName = "report-writer";

    [Fact]
    public async Task CreateAsync_MaterializesPackagesAndExposesScopedTools()
    {
        var fixture = new FactoryFixture();
        await using AgentSkillsRuntime runtime = await fixture.CreateRuntimeAsync(agentCodeExecution: true, scriptExecutionEnabled: true);

        RuntimeSkill skill = Assert.Single(runtime.Skills)!;
        Assert.Equal(SkillName, skill.Name);
        Assert.True(File.Exists(Path.Combine(skill.Path, "SKILL.md")), "materialized package must contain SKILL.md");

        AIFunction reader = GetTool(runtime, "read_skill_file");
        // AIFunctionFactory 会把返回值包装为 JsonElement（字符串结果为 String 元素）。
        JsonElement markdownElement = Assert.IsType<JsonElement>(await reader.InvokeAsync(new AIFunctionArguments
        {
            ["skill"] = SkillName,
            ["path"] = "SKILL.md"
        }));
        string markdown = markdownElement.ValueKind == JsonValueKind.String
            ? markdownElement.GetString() ?? markdownElement.GetRawText()
            : markdownElement.GetRawText();
        Assert.Contains("Report writer", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateAsync_MaterializesLegacyNestedIndexWithRootSkillMarkdown()
    {
        var fixture = new FactoryFixture();
        await using AgentSkillsRuntime runtime =
            await fixture.CreateRuntimeAsync(agentCodeExecution: true, scriptExecutionEnabled: true, nestedIndex: true);

        RuntimeSkill skill = Assert.Single(runtime.Skills)!;
        Assert.True(File.Exists(Path.Combine(skill.Path, "SKILL.md")),
            "legacy nested storage index must materialize with SKILL.md at the package root");

        AIFunction reader = GetTool(runtime, "read_skill_file");
        JsonElement markdownElement = Assert.IsType<JsonElement>(await reader.InvokeAsync(new AIFunctionArguments
        {
            ["skill"] = SkillName,
            ["path"] = "SKILL.md"
        }));
        string markdown = markdownElement.ValueKind == JsonValueKind.String
            ? markdownElement.GetString() ?? markdownElement.GetRawText()
            : markdownElement.GetRawText();
        Assert.Contains("Report writer", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunSkillScript_ExecutesPackageScriptThroughIsolatedRunner()
    {
        var fixture = new FactoryFixture();
        await using AgentSkillsRuntime runtime = await fixture.CreateRuntimeAsync(agentCodeExecution: true, scriptExecutionEnabled: true);

        AIFunction runner = GetTool(runtime, "run_skill_script");
        object? result = await runner.InvokeAsync(new AIFunctionArguments
        {
            ["skill"] = SkillName,
            ["script"] = "scripts/analyze.py"
        });

        CodeExecutionRequest request = Assert.Single(fixture.Executor.Requests);
        Assert.Contains("runpy.run_path(\"/input/scripts/analyze.py\"", request.Code, StringComparison.Ordinal);
        Assert.Contains("sys.path.insert(0, \"/input\")", request.Code, StringComparison.Ordinal);
        Assert.Contains(request.Files, file => file.Name.EndsWith("analyze.py", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("SKILL.md", request.Files.Select(file => file.Name), StringComparer.OrdinalIgnoreCase);
        Assert.Contains("exitCode", result?.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunSkillScript_RejectsPathOutsidePackage()
    {
        var fixture = new FactoryFixture();
        await using AgentSkillsRuntime runtime = await fixture.CreateRuntimeAsync(agentCodeExecution: true, scriptExecutionEnabled: true);

        AIFunction runner = GetTool(runtime, "run_skill_script");
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await runner.InvokeAsync(new AIFunctionArguments
        {
            ["skill"] = SkillName,
            ["script"] = "../outside.py"
        }));
        Assert.Empty(fixture.Executor.Requests);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task WithoutDoubleOptIn_ScriptToolIsNotExposed(
        bool agentCodeExecution, bool scriptExecutionEnabled, bool hostCodeExecutionEnabled)
    {
        var fixture = new FactoryFixture(hostCodeExecutionEnabled);
        await using AgentSkillsRuntime runtime = await fixture.CreateRuntimeAsync(agentCodeExecution, scriptExecutionEnabled);

        Assert.DoesNotContain(runtime.Tools, tool => tool.Name == "run_skill_script");
        Assert.Empty(fixture.Executor.Requests);
    }

    private static AIFunction GetTool(AgentSkillsRuntime runtime, string name) =>
        Assert.IsAssignableFrom<AIFunction>(Assert.Single(runtime.Tools, tool => tool.Name == name));

    private sealed class FactoryFixture
    {
        internal RecordingFileAssetRepository Repository { get; } = new();
        internal RecordingFileObjectStore Objects { get; } = new();
        internal StubSkillCatalog Catalog { get; } = new();
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
                Options.Create(new CodeExecutionOptions { Enabled = hostCodeExecutionEnabled }));
        }

        internal async Task<AgentSkillsRuntime> CreateRuntimeAsync(
            bool agentCodeExecution,
            bool scriptExecutionEnabled,
            bool nestedIndex = false)
        {
            Context.Set(TurnContexts.Create());
            RegisterPackage(nestedIndex);
            return await Factory.CreateRuntimeAsync(
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

        private void RegisterPackage(bool nestedIndex)
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
            // 归一化之前上传的记录原样保留 zip 的顶层包裹目录：SKILL.md 嵌套在包名目录下。
            string wrapper = nestedIndex ? SkillName + "/" : string.Empty;
            var index = new SkillPackageStorageIndex
            {
                TenantId = "tenant",
                Files =
                [
                    new SkillPackageStorageFile
                    {
                        RelativePath = wrapper + "SKILL.md",
                        ObjectKey = baseKey + "/0",
                        Sha256 = Convert.ToHexString(SHA256.HashData(skillMd)).ToLowerInvariant()
                    },
                    new SkillPackageStorageFile
                    {
                        RelativePath = wrapper + "scripts/analyze.py",
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

    private sealed class StubSkillCatalog : ISkillCatalog
    {
        private readonly List<SkillInstanceConfig> _skills = [];

        internal void Register(SkillInstanceConfig skill) => _skills.Add(skill);

        public Task<IReadOnlyList<SkillInstanceConfig>> ListAsync(
            string tenantId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SkillInstanceConfig>>(
                _skills.Where(skill => string.Equals(skill.TenantId, tenantId, StringComparison.Ordinal)).ToList());

        public Task<SkillInstanceConfig?> GetAsync(
            string tenantId,
            string skillId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_skills.FirstOrDefault(skill =>
                string.Equals(skill.TenantId, tenantId, StringComparison.Ordinal)
                && string.Equals(skill.Id, skillId, StringComparison.Ordinal)));
    }
}
