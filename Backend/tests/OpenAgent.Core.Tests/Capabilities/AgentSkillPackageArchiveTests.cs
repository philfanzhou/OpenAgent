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
    public void InspectAsync_ReadsHumanApprovalDeclarationFromSkillMetadata()
    {
        byte[] package = CreateArchive(archive =>
            WriteEntry(
                archive,
                "production/SKILL.md",
                "---\nname: production\ndescription: Changes production\nrequires-human-approval: true\n---\n"));

        AgentSkillPackageMetadata metadata = AgentSkillPackageArchive.Inspect(package, default);

        Assert.True(metadata.RequiresHumanApproval);
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
