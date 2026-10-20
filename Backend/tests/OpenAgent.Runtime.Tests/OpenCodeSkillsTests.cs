using OpenAgent.Contracts.Runtime;
using OpenAgent.Runtime;
using Xunit;

namespace OpenAgent.Runtime.Tests;

public sealed class OpenCodeSkillsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "opencode-skills-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Materialize_CreatesSkillEntriesUnderOpenCodeSkillsDirectory()
    {
        string package = CreatePackage("report-writer");
        string workspace = Path.Combine(_root, "ws-1");
        Directory.CreateDirectory(workspace);

        OpenCodeSkills.Materialize(workspace, [new RuntimeSkill("report-writer", package)]);

        string entry = Path.Combine(workspace, ".opencode", "skills", "report-writer", "SKILL.md");
        Assert.True(File.Exists(entry), "SKILL.md must be reachable through the workspace entry");
        Assert.Contains("Writes reports", File.ReadAllText(entry), StringComparison.Ordinal);
    }

    [Fact]
    public void Materialize_MultipleSkills_KeepSeparateEntries()
    {
        string first = CreatePackage("alpha-skill");
        string second = CreatePackage("beta-skill");
        string workspace = Path.Combine(_root, "ws-2");
        Directory.CreateDirectory(workspace);

        OpenCodeSkills.Materialize(workspace, [new RuntimeSkill("alpha-skill", first), new RuntimeSkill("beta-skill", second)]);

        Assert.True(File.Exists(Path.Combine(workspace, ".opencode", "skills", "alpha-skill", "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(workspace, ".opencode", "skills", "beta-skill", "SKILL.md")));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("bad\\name")]
    public void Materialize_PathSeparatorInName_Rejected(string name)
    {
        string package = CreatePackage("safe");
        string workspace = Path.Combine(_root, "ws-3");
        Directory.CreateDirectory(workspace);

        Assert.Throws<InvalidOperationException>(() =>
            OpenCodeSkills.Materialize(workspace, [new RuntimeSkill(name, package)]));
        Assert.False(Directory.Exists(Path.Combine(workspace, ".opencode", "skills", "..")));
    }

    [Fact]
    public void Materialize_MissingPackageDirectory_Fails()
    {
        string workspace = Path.Combine(_root, "ws-4");
        Directory.CreateDirectory(workspace);

        Assert.Throws<InvalidOperationException>(() =>
            OpenCodeSkills.Materialize(workspace, [new RuntimeSkill("ghost", Path.Combine(_root, "missing"))]));
    }

    private string CreatePackage(string name)
    {
        string package = Path.Combine(_root, "packages", name);
        Directory.CreateDirectory(package);
        File.WriteAllText(Path.Combine(package, "SKILL.md"), $"""
            ---
            name: {name}
            description: Writes reports from templates.
            ---

            # Skill body
            """);
        return package;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 符号链接目录在部分平台删除语义不同；测试残留交给系统临时目录清理。
        }
    }
}

public sealed class OpenCodeSessionMcpTests
{
    [Fact]
    public void Map_ProducesRemoteEntriesForHttpAndSseServers()
    {
        OpenAgent.Runtime.Acp.Protocol.McpServer[] mapped = OpenCodeSessionMcp.Map(
        [
            new RuntimeMcpServer("localmcp", "http://127.0.0.1:9/mcp", "http"),
            new RuntimeMcpServer("events", "http://127.0.0.1:9/sse", "sse")
        ]);

        Assert.Equal(2, mapped.Length);
        Assert.Equal("localmcp", mapped[0].Name);
        Assert.Equal("http", mapped[0].Type);
        Assert.NotNull(mapped[0].Headers);
        Assert.Empty(mapped[0].Headers!);
        Assert.Equal("http://127.0.0.1:9/mcp", mapped[0].Url);
        Assert.Equal("sse", mapped[1].Type);
    }

    [Fact]
    public void Map_EmptyOrNull_ReturnsNoEntries()
    {
        Assert.Empty(OpenCodeSessionMcp.Map(null));
        Assert.Empty(OpenCodeSessionMcp.Map([]));
    }
}
