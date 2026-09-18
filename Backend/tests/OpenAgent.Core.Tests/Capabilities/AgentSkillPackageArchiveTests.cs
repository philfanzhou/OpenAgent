using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Core.Capabilities.Skill;
using Xunit;

namespace OpenAgent.Core.Tests.Capabilities;

public sealed class AgentSkillPackageArchiveTests
{
    [Fact]
    public void InspectAsync_UsesOfficialSkillFrontmatter()
    {
        byte[] package = CreatePackage("customer-lookup", "Looks up customers");

        AgentSkillPackageMetadata metadata = AgentSkillPackageArchive.Inspect(package, default);

        Assert.Equal("customer-lookup", metadata.Name);
        Assert.Equal("Looks up customers", metadata.Description);
        Assert.Equal(1, metadata.SkillCount);
        Assert.Equal(0, metadata.ResourceCount);
    }

    [Fact]
    public void InspectAsync_RejectsArchiveWithoutSkillFile()
    {
        byte[] package;
        using (var stream = new MemoryStream())
        {
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            using (StreamWriter writer = new(archive.CreateEntry("README.md").Open(), Encoding.UTF8))
            {
                writer.Write("not a skill");
            }
            package = stream.ToArray();
        }

        Assert.Throws<InvalidOperationException>(() =>
            AgentSkillPackageArchive.Inspect(package, default));
    }

    [Fact]
    public void InspectAsync_CountsResources()
    {
        byte[] package = CreateArchive(archive =>
        {
            WriteEntry(archive, "analysis/SKILL.md", "---\nname: analysis\ndescription: Analyze data\n---\n");
            WriteEntry(archive, "analysis/resources/sample.csv", "value\n42\n");
        });

        AgentSkillPackageMetadata metadata = AgentSkillPackageArchive.Inspect(package, default);

        Assert.Equal(1, metadata.ResourceCount);
    }

    [Fact]
    public void InspectAsync_InventoriesPythonScripts()
    {
        byte[] package = CreateArchive(archive =>
        {
            WriteEntry(archive, "analysis/SKILL.md", "---\nname: analysis\ndescription: Analyze data\n---\n");
            WriteEntry(archive, "analysis/scripts/run.py", "print('run')");
            WriteEntry(archive, "analysis/scripts/helper.PY", "print('helper')");
            WriteEntry(archive, "analysis/resources/sample.csv", "value\n42\n");
            WriteEntry(archive, "analysis/scripts/run.py.txt", "not a script");
        });

        AgentSkillPackageMetadata metadata = AgentSkillPackageArchive.Inspect(package, default);

        Assert.Equal(
            ["analysis/scripts/helper.PY", "analysis/scripts/run.py"],
            metadata.ScriptNames);
    }

    [Fact]
    public void InspectAsync_InventoriesJavaScriptScripts()
    {
        byte[] package = CreateArchive(archive =>
        {
            WriteEntry(archive, "report/SKILL.md", "---\nname: report\ndescription: Report writer\n---\n");
            WriteEntry(archive, "report/scripts/run.js", "console.log('run')");
            WriteEntry(archive, "report/scripts/helper.mjs", "export const x = 1;");
            WriteEntry(archive, "report/scripts/run.js.txt", "not a script");
            WriteEntry(archive, "report/scripts/greet.sh", "echo nope");
        });

        AgentSkillPackageMetadata metadata = AgentSkillPackageArchive.Inspect(package, default);

        Assert.Equal(
            ["report/scripts/helper.mjs", "report/scripts/run.js"],
            metadata.ScriptNames);
    }

    [Fact]
    public void InspectMarkdown_HasNoScriptInventory()
    {
        AgentSkillPackageMetadata metadata = AgentSkillPackageArchive.InspectMarkdown(
            Encoding.UTF8.GetBytes("---\nname: text-only\ndescription: No scripts\n---\n# Instructions\n"),
            default);

        Assert.Empty(metadata.ScriptNames);
    }

    [Fact]
    public void ReadZipFiles_RejectsTooManyFiles()
    {
        byte[] package = CreateArchive(archive =>
        {
            WriteEntry(archive, "many/SKILL.md", "---\nname: many\ndescription: Many files\n---\n");
            for (int index = 0; index < AgentSkillPackageArchive.MaxFileCount; index++)
            {
                WriteEntry(archive, $"many/resources/{index}.txt", "x");
            }
        });

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            AgentSkillPackageArchive.ReadZipFiles(package, default));

        Assert.Contains("more than", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadZipFiles_RejectsExpandedArchiveOverLimit()
    {
        byte[] package = CreateArchive(archive =>
            WriteEntry(
                archive,
                "large/SKILL.md",
                new string('a', AgentSkillPackageArchive.MaxExpandedBytes + 1)));

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            AgentSkillPackageArchive.ReadZipFiles(package, default));

        Assert.Contains("exceeds", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] CreatePackage(string name, string description)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        using (StreamWriter writer = new(archive.CreateEntry($"{name}/SKILL.md").Open(), Encoding.UTF8))
        {
            writer.Write($"---\nname: {name}\ndescription: {description}\n---\n\n# Instructions\nUse the customer system.\n");
        }
        return stream.ToArray();
    }

    private static byte[] CreateArchive(Action<ZipArchive> write)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            write(archive);
        }
        return stream.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string path, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(path).Open(), Encoding.UTF8);
        writer.Write(content);
    }
}
