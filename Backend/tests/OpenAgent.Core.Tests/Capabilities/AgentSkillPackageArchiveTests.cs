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
}
