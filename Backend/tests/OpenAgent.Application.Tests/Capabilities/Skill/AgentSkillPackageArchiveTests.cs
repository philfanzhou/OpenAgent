using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Application.Capabilities.Skill;
using OpenAgent.Contracts.Skills;
using Xunit;

namespace OpenAgent.Application.Tests.Capabilities;

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

    [Fact]
    public void ReadZipFiles_StripsWrapperDirectoryAroundSkillMarkdown()
    {
        byte[] package = CreateArchive(archive =>
        {
            WriteEntry(archive, "customer-lookup/SKILL.md", "---\nname: customer-lookup\ndescription: Looks up customers\n---\n");
            WriteEntry(archive, "customer-lookup/scripts/lookup.py", "print('lookup')");
        });

        IReadOnlyList<SkillPackageFile> files = AgentSkillPackageArchive.ReadZipFiles(package, default);

        Assert.Equal(["SKILL.md", "scripts/lookup.py"], files.Select(file => file.RelativePath).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ReadZipFiles_KeepsRootedLayoutUnchanged()
    {
        byte[] package = CreateArchive(archive =>
        {
            WriteEntry(archive, "SKILL.md", "---\nname: customer-lookup\ndescription: Looks up customers\n---\n");
            WriteEntry(archive, "scripts/lookup.py", "print('lookup')");
        });

        IReadOnlyList<SkillPackageFile> files = AgentSkillPackageArchive.ReadZipFiles(package, default);

        Assert.Equal(["SKILL.md", "scripts/lookup.py"], files.Select(file => file.RelativePath).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ReadZipFiles_MixedRootedAndWrappedFiles_Rejected()
    {
        byte[] package = CreateArchive(archive =>
        {
            WriteEntry(archive, "customer-lookup/SKILL.md", "---\nname: customer-lookup\ndescription: Looks up customers\n---\n");
            WriteEntry(archive, "README.md", "outside the wrapper");
        });

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            AgentSkillPackageArchive.ReadZipFiles(package, default));

        Assert.Contains("root", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NormalizeStorageLayout_RewritesRelativePathsInPlace()
    {
        var files = new List<SkillPackageStorageFile>
        {
            new() { RelativePath = "report-writer/SKILL.md", ObjectKey = "a/0", Sha256 = "00" },
            new() { RelativePath = "report-writer/scripts/analyze.py", ObjectKey = "a/1", Sha256 = "01" }
        };

        AgentSkillPackageArchive.NormalizeStorageLayout(files);

        Assert.Equal("SKILL.md", files[0].RelativePath);
        Assert.Equal("scripts/analyze.py", files[1].RelativePath);
        // 只改写相对路径：对象键与校验和保持原值。
        Assert.Equal("a/0", files[0].ObjectKey);
        Assert.Equal("a/1", files[1].ObjectKey);
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
