using OpenAgent.Capabilities.Tests.TestDoubles;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using Microsoft.Extensions.AI;
using Moq;
using OpenAgent.Contracts.Capabilities;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Runtime;
using OpenAgent.Contracts.Security;
using OpenAgent.Contracts.Skills;
using OpenAgent.Application.Capabilities.Skill;
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
        await using AgentSkillsRuntime runtime = await fixture.CreateRuntimeAsync();

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
        await using AgentSkillsRuntime runtime = await fixture.CreateRuntimeAsync(nestedIndex: true);

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
    public async Task ReadSkillFile_RejectsPathOutsidePackage()
    {
        var fixture = new FactoryFixture();
        await using AgentSkillsRuntime runtime = await fixture.CreateRuntimeAsync();

        AIFunction reader = GetTool(runtime, "read_skill_file");
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await reader.InvokeAsync(new AIFunctionArguments
        {
            ["skill"] = SkillName,
            ["path"] = "../outside.md"
        }));
    }

    private static AIFunction GetTool(AgentSkillsRuntime runtime, string name) =>
        Assert.IsAssignableFrom<AIFunction>(Assert.Single(runtime.Tools, tool => tool.Name == name));

    private sealed class FactoryFixture
    {
        internal RecordingFileObjectStore Objects { get; } = new();
        internal StubSkillCatalog Catalog { get; } = new();
        internal AgentUserContext User { get; } = new() { TenantId = "tenant", UserId = "user" };
        internal AgentSkillsProviderFactory Factory { get; }

        internal FactoryFixture()
        {
            var auth = new Mock<IAgentAuthorizationService>();
            auth.Setup(service => service.IsAuthorizedAsync(
                    It.IsAny<AgentAuthorizationRequest>(), It.IsAny<IAgentUserContext>(), It.IsAny<CancellationToken>()))
                .Returns(Task.FromResult(true));
            Factory = new AgentSkillsProviderFactory(
                Objects,
                Catalog,
                new AgentAuthorizationGate(auth.Object));
        }

        internal async Task<AgentSkillsRuntime> CreateRuntimeAsync(bool nestedIndex = false)
        {
            RegisterPackage(nestedIndex);
            return await Factory.CreateRuntimeAsync(
                "agent",
                new AgentConfig
                {
                    Skills = new SkillsConfig
                    {
                        Instances = [NewInstance()]
                    }
                },
                User,
                CancellationToken.None);
        }

        private static SkillInstanceConfig NewInstance() => new()
        {
            TenantId = "tenant",
            Id = "skill-1",
            Name = SkillName,
            Type = SkillTypes.AgentSkill,
            Enabled = true,
            ObjectKey = "files/tenants/" + FileObjectTenantScope.CreatePartition("tenant") + "/skill-packages/skill-1",
            PackageFormat = "directory"
        };

        private void RegisterPackage(bool nestedIndex)
        {
            SkillInstanceConfig instance = NewInstance();
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
