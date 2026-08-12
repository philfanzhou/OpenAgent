using System.IO.Compression;
using System.Text;
using OpenAgent.Contracts.Skills;
using OpenAgent.Core.Capabilities.Skill;
using Xunit;

namespace OpenAgent.Core.Tests.Capabilities;

public class SkillPackageReaderTests
{
    public static TheoryData<string, string, string> ManifestFormats => new()
    {
        {
            "skill.json",
            "{\"id\":\"weather\",\"name\":\"Weather\",\"endpointUrl\":\"https://skills.example.test/weather\",\"parametersJsonSchema\":\"{\\\"type\\\":\\\"object\\\"}\"}",
            "json"
        },
        {
            "skill.yaml",
            "\uFEFFid: weather\nname: Weather\nendpointUrl: https://skills.example.test/weather\nparametersJsonSchema: '{\"type\":\"object\"}'",
            "yaml"
        },
        {
            "SKILL.md",
            "---\nid: weather\nname: Weather\nendpointUrl: https://skills.example.test/weather\nparametersJsonSchema: '{\"type\":\"object\"}'\n---\nCalls the weather service.",
            "markdown"
        }
    };

    [Theory]
    [MemberData(nameof(ManifestFormats))]
    public void Read_SupportedManifestFormat_ReturnsManifest(
        string fileName,
        string content,
        string expectedFormat)
    {
        var reader = new SkillPackageReader();

        SkillPackageManifest manifest = reader.Read(fileName, Encoding.UTF8.GetBytes(content));

        Assert.Equal("weather", manifest.Id);
        Assert.Equal("Weather", manifest.Name);
        Assert.Equal(expectedFormat, reader.GetFormat(fileName));
    }

    [Fact]
    public void Read_ZipContainingMarkdownManifest_ReturnsManifest()
    {
        var reader = new SkillPackageReader();
        byte[] package = CreateZip(
            "nested/SKILL.md",
            "---\nid: weather\nname: Weather\nendpointUrl: https://skills.example.test/weather\n---\nWeather lookup.");

        SkillPackageManifest manifest = reader.Read("weather.zip", package);

        Assert.Equal("weather", manifest.Id);
        Assert.Equal("zip", reader.GetFormat("weather.zip"));
    }

    private static byte[] CreateZip(string entryName, string content)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry entry = archive.CreateEntry(entryName);
            using Stream output = entry.Open();
            output.Write(Encoding.UTF8.GetBytes(content));
        }

        return buffer.ToArray();
    }
}
